// Translated from behavioral/dynamodb/check_batch_get_item_full_response_at_max_key_count_returns_exact_capacity.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-get-item-full-response-at-max-key-count-returns-exact-capacity</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchGetItemFullResponseAtMaxKeyCountReturnsExactCapacity
{
    private const string Check = "batch-get-item-full-response-at-max-key-count-returns-exact-capacity";

    // 100 keys is BatchGetItem's per-call maximum. Each item is exactly 15 of DynamoDB's 4096-byte
    // read-capacity blocks (61440 bytes), so the 100-item response (about 6 MB) stays under the 16 MB
    // response limit and the expected capacity is a whole number of blocks per item.
    private const int NItems = 100;
    private const int Band = 15 * 4096;

    /// <summary>
    /// A BatchGetItem for the 100-key maximum, with 60 KB items, comes back complete in one
    /// response with ConsumedCapacity billed as the exact sum of each item's own 4KB-rounded read
    /// cost, halved for an eventually consistent read.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.batch-get-item-full-response-at-max-key-count-returns-exact-capacity")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-bginearcap-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        var keys = Enumerable.Range(0, NItems).Select(i => $"k{i:000}").ToList();
        var canaryPad = PadFor(Band, keys[0]);
        var canaryPut = await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = Item(("pk", S(keys[0])), ("pad", S(canaryPad))),
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        });
        var canaryWcu = canaryPut.ConsumedCapacity.CapacityUnits;
        Ensure(
            canaryWcu == Band / 1024.0,
            $"the canary write of the {Band}-byte item billed {Py.Repr(canaryWcu)} WCU, expected "
            + $"{Py.Repr(Band / 1024.0)} -- the capacity math below rests on this item actually being that size");
        foreach (var key in keys.Skip(1))
        {
            await ddb.PutItemAsync(table, Item(("pk", S(key)), ("pad", S(PadFor(Band, key)))));
        }

        var perItemRcu = Band / 4096.0;
        var expectedStrongCu = perItemRcu * NItems;

        foreach (var (consistentRead, expectedCu) in new[] { (true, expectedStrongCu), (false, expectedStrongCu / 2) })
        {
            var consistentRepr = consistentRead ? "True" : "False";
            var resp = await ddb.BatchGetItemAsync(new BatchGetItemRequest
            {
                RequestItems = new()
                {
                    [table] = new KeysAndAttributes { Keys = [.. keys.Select(k => Item(("pk", S(k))))], ConsistentRead = consistentRead },
                },
                ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
            });
            var unprocessed = resp.UnprocessedKeys ?? [];
            Ensure(
                unprocessed.Count == 0,
                $"ConsistentRead={consistentRepr}: expected all {NItems} items to return in one "
                + $"response with no UnprocessedKeys, got {Py.Repr(unprocessed.ToDictionary(e => e.Key, e => e.Value.Keys.Count))}");
            var items = resp.Responses[table];
            Ensure(items.Count == NItems, $"ConsistentRead={consistentRepr}: expected {NItems} items back, got {items.Count}");
            var returned = items.ToDictionary(item => item["pk"].S, item => item["pad"].S, StringComparer.Ordinal);
            Ensure(
                returned.Keys.ToHashSet().SetEquals(keys),
                $"ConsistentRead={consistentRepr}: symmetric difference "
                + $"{Py.Repr(returned.Keys.Except(keys).Concat(keys.Except(returned.Keys)).Order(StringComparer.Ordinal))}");
            var badLengths = returned.Where(e => e.Value.Length != canaryPad.Length).ToDictionary(e => e.Key, e => e.Value.Length);
            Ensure(badLengths.Count == 0, $"ConsistentRead={consistentRepr}: items truncated to fit the response: {Py.Repr(badLengths)}");
            var cu = resp.ConsumedCapacity[0].CapacityUnits;
            Ensure(
                cu == expectedCu,
                $"ConsistentRead={consistentRepr}: BatchGetItem over {NItems} items of {Band} "
                + $"bytes each billed {Py.Repr(cu)} CapacityUnits, expected {Py.Repr(expectedCu)} "
                + $"({NItems} items x {Py.Repr(perItemRcu)} RCU each, halved for eventual consistency)");
        }
    }

    /// <summary>Padding that lands the whole item ("pk" name+value, "pad" name+value) at exactly band bytes.</summary>
    private static string PadFor(int band, string pk) => new('x', band - 2 - pk.Length - 3);

    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };
}

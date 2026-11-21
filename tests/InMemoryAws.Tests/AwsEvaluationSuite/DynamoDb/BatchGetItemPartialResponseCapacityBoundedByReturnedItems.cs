// Translated from behavioral/dynamodb/check_batch_get_item_partial_response_capacity_bounded_by_returned_items.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-get-item-partial-response-capacity-bounded-by-returned-items</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchGetItemPartialResponseCapacityBoundedByReturnedItems
{
    private const string Check = "batch-get-item-partial-response-capacity-bounded-by-returned-items";

    // 44 items of 95 of DynamoDB's 4096-byte read-capacity blocks (389120 bytes) total about 17.1 MB,
    // over the 16 MB BatchGetItem response ceiling, so the call answers partial.
    private const int NItems = 44;
    private const int Band = 95 * 4096;
    private const double PerItemRcu = Band / 4096.0;

    /// <summary>
    /// A BatchGetItem too large for one response bills ConsumedCapacity within one item's cost
    /// of just the items that response actually returns, never anywhere near the cost of the full
    /// set of keys requested.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.batch-get-item-partial-response-capacity-bounded-by-returned-items")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-bgipartcap-");
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

        var allKeys = Enumerable.Range(0, NItems).Select(i => $"k{i:00}").ToList();
        var canaryPad = PadFor(Band, allKeys[0]);
        var canaryPut = await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = Item(("pk", S(allKeys[0])), ("pad", S(canaryPad))),
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        });
        var canaryWcu = canaryPut.ConsumedCapacity.CapacityUnits;
        Ensure(
            canaryWcu == Band / 1024.0,
            $"the canary write of the {Band}-byte item billed {Py.Repr(canaryWcu)} WCU, expected "
            + $"{Py.Repr(Band / 1024.0)} -- the capacity math below rests on every item being that size");
        foreach (var key in allKeys.Skip(1))
        {
            await ddb.PutItemAsync(table, Item(("pk", S(key)), ("pad", S(PadFor(Band, key)))));
        }

        var pending = new Dictionary<string, KeysAndAttributes>
        {
            [table] = new KeysAndAttributes { Keys = [.. allKeys.Select(k => Item(("pk", S(k))))], ConsistentRead = true },
        };
        var seen = new List<string>();
        var rounds = 0;
        while (pending.Count > 0)
        {
            rounds++;
            Ensure(rounds <= 10, $"the drain loop did not converge in {rounds} rounds; {seen.Count} of {NItems} seen");
            var page = await ddb.BatchGetItemAsync(new BatchGetItemRequest { RequestItems = pending, ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL });
            var pagePks = page.Responses[table].Select(i => i["pk"].S).ToList();
            if (rounds == 1)
            {
                Ensure(
                    pagePks.Count < NItems,
                    $"{NItems} items of {Band} bytes must not fit one response, but the first page returned all of them");
            }

            var lower = PerItemRcu * pagePks.Count;
            var upper = PerItemRcu * (pagePks.Count + 1);
            const double fullRequestCu = PerItemRcu * NItems;
            var pageCu = page.ConsumedCapacity[0].CapacityUnits;
            Ensure(
                lower <= pageCu && pageCu < upper,
                $"round {rounds}: a page returning {pagePks.Count} item(s) of {Band} bytes each "
                + $"billed {Py.Repr(pageCu)} CapacityUnits, expected it within [{Py.Repr(lower)}, {Py.Repr(upper)}) -- the "
                + "cost of just the returned items, plus at most one more item's worth of slack");
            Ensure(
                pageCu < fullRequestCu,
                $"round {rounds}: a page returning {pagePks.Count} of the {NItems} requested items "
                + $"billed {Py.Repr(pageCu)} CapacityUnits, which must stay below the full request's "
                + $"{Py.Repr(fullRequestCu)} -- the partial response must not be billed as if it answered every requested key");
            seen.AddRange(pagePks);
            pending = (page.UnprocessedKeys ?? []).Where(e => e.Value.Keys is { Count: > 0 }).ToDictionary(e => e.Key, e => e.Value);
        }

        Ensure(
            seen.Order(StringComparer.Ordinal).SequenceEqual(allKeys.Order(StringComparer.Ordinal)),
            "draining UnprocessedKeys did not yield exactly the requested set: symmetric difference "
            + $"{Py.Repr(seen.Except(allKeys).Concat(allKeys.Except(seen)).Order(StringComparer.Ordinal))}");
        Ensure(
            seen.Count == NItems,
            $"the drain returned an item more than once: {seen.Count - seen.Distinct().Count()} duplicates in {seen.Count} results");
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

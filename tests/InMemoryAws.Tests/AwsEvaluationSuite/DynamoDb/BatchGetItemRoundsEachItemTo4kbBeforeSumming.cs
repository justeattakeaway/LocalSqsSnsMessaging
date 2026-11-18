// Translated from behavioral/dynamodb/check_batch_get_item_rounds_each_item_to_4kb_before_summing.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-get-item-rounds-each-item-to-4kb-before-summing</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchGetItemRoundsEachItemTo4kbBeforeSumming
{
    private const string Check = "batch-get-item-rounds-each-item-to-4kb-before-summing";
    private const int Kb = 1024;
    private const int SmallBand = 1 * Kb;
    private const int LargeBand = 5 * Kb;

    /// <summary>
    /// BatchGetItem rounds each item up to its own 4KB block and then totals the blocks, rather
    /// than summing bytes first and rounding once; INDEXES reports the same total under Table.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/read-write-operations.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.batch-get-item-rounds-each-item-to-4kb-before-summing")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbbgiround-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 1, maxAttempts: 90);

        const string smallPk = "small";
        const string largePk = "large";
        foreach (var (pk, band) in new[] { (smallPk, SmallBand), (largePk, LargeBand) })
        {
            var put = await ddb.PutItemAsync(new PutItemRequest
            {
                TableName = table,
                Item = Item(("pk", S(pk)), ("pad", S(PadFor(band, pk)))),
                ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
            });
            var gotWcu = put.ConsumedCapacity.CapacityUnits;
            Ensure(
                gotWcu == (double)band / Kb,
                $"the canary write of the {band}-byte {Py.Repr(pk)} item billed {Py.Repr(gotWcu)} WCU, expected " +
                $"{Py.Repr((double)band / Kb)} -- the read assertions below rest on this item actually being that size");
        }

        List<Dictionary<string, AttributeValue>> Keys() => [Item(("pk", S(smallPk))), Item(("pk", S(largePk)))];

        foreach (var rcc in new[] { "TOTAL", "INDEXES" })
        {
            var strong = await ddb.BatchGetItemAsync(new BatchGetItemRequest
            {
                RequestItems = new() { [table] = new KeysAndAttributes { Keys = Keys(), ConsistentRead = true } },
                ReturnConsumedCapacity = rcc,
            });
            var eventual = await ddb.BatchGetItemAsync(new BatchGetItemRequest
            {
                RequestItems = new() { [table] = new KeysAndAttributes { Keys = Keys(), ConsistentRead = false } },
                ReturnConsumedCapacity = rcc,
            });
            foreach (var (label, resp) in new[] { ("strong", strong), ("eventual", eventual) })
            {
                var unprocessed = resp.UnprocessedKeys ?? [];
                Ensure(
                    unprocessed.Count == 0,
                    $"{rcc} {label} BatchGetItem left UnprocessedKeys {Py.Repr(unprocessed.Keys)}; its capacity " +
                    "number therefore covers fewer than both items and cannot be compared");
                var returned = resp.Responses[table].Select(item => item["pk"].S).ToHashSet(StringComparer.Ordinal);
                Ensure(
                    returned.SetEquals([smallPk, largePk]),
                    $"{rcc} {label} BatchGetItem returned {Py.Repr(returned.Order(StringComparer.Ordinal).ToList())}, expected both items");
            }

            var strongCu = strong.ConsumedCapacity[0].CapacityUnits;
            var eventualCu = eventual.ConsumedCapacity[0].CapacityUnits;
            Ensure(
                strongCu == 3.0,
                $"{rcc}: a strongly consistent BatchGetItem over a 1KB and a 5KB item billed " +
                $"{Py.Repr(strongCu)}, expected 3.0 (1 block for the 1KB item plus 2 for the 5KB item, " +
                "each item rounded up on its own) -- 2.0 would mean the 6KB combined total was " +
                "summed first and rounded once");
            Ensure(
                eventualCu == 1.5,
                $"{rcc}: an eventually consistent BatchGetItem over the same two items billed " +
                $"{Py.Repr(eventualCu)}, expected 1.5 (half of the strongly consistent 3.0)");

            if (rcc == "INDEXES")
            {
                var strongTableCu = strong.ConsumedCapacity[0].Table.CapacityUnits;
                var eventualTableCu = eventual.ConsumedCapacity[0].Table.CapacityUnits;
                Ensure(
                    strongTableCu == strongCu,
                    $"INDEXES strong Table.CapacityUnits was {Py.Repr(strongTableCu)}, expected it to equal " +
                    $"the overall total {Py.Repr(strongCu)}");
                Ensure(
                    eventualTableCu == eventualCu,
                    $"INDEXES eventual Table.CapacityUnits was {Py.Repr(eventualTableCu)}, expected it to " +
                    $"equal the overall total {Py.Repr(eventualCu)}");
            }
        }

        // Positive control: a Query over the lone 1KB item bills the single block BatchGetItem
        // charged that item above.
        var queryStrong = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :p",
            ConsistentRead = true,
            ExpressionAttributeValues = Item((":p", S(smallPk))),
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        });
        Ensure(
            queryStrong.ConsumedCapacity.CapacityUnits == 1.0,
            $"a Query over the lone 1KB item billed {Py.Repr(queryStrong.ConsumedCapacity.CapacityUnits)}, " +
            "expected 1.0");
    }

    /// <summary>
    /// Padding that lands the whole item just inside `band` bytes.
    ///
    /// Overhead accounted for: the "pk" name (2) + its value, and the "pad" name (3).
    /// </summary>
    private static string PadFor(int band, string pk) => new('x', band - 2 - pk.Length - 3);
}

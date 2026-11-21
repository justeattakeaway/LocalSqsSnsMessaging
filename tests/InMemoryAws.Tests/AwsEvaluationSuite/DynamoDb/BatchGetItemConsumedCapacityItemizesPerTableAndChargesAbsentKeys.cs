// Translated from behavioral/dynamodb/check_batch_get_item_consumed_capacity_itemizes_per_table_and_charges_absent_keys.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-get-item-consumed-capacity-itemizes-per-table-and-charges-absent-keys</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchGetItemConsumedCapacityItemizesPerTableAndChargesAbsentKeys
{
    private const string Check = "batch-get-item-consumed-capacity-itemizes-per-table-and-charges-absent-keys";
    private const int Kb = 1024;
    private const int RcuBlock = 4 * Kb;
    private const int SmallBand = 1 * Kb;
    private const int LargeBand = 5 * Kb;

    /// <summary>
    /// A single BatchGetItem spanning two tables returns one ConsumedCapacity entry per table,
    /// each still rounding its own items to 4KB blocks and charging a key that misses exactly like a
    /// key that hits; INDEXES reports the same per-table total under Table.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/read-write-operations.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.batch-get-item-consumed-capacity-itemizes-per-table-and-charges-absent-keys")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var tableA = await SimpleTable(ctx, ddb, "sim2real-behav-ddbmcap-a-");
        var tableB = await SimpleTable(ctx, ddb, "sim2real-behav-ddbmcap-b-");

        var (presentA, presentB, absentB) = ("present", "present", "absent");

        var putA = await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = tableA,
            Item = Item(("pk", S(presentA)), ("pad", S(PadFor(SmallBand, presentA)))),
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        });
        var wcuA = putA.ConsumedCapacity.CapacityUnits;
        Ensure(
            wcuA == (double)SmallBand / Kb,
            $"the canary write of the {SmallBand}-byte item on table_a billed {Py.Repr(wcuA)} WCU, "
            + $"expected {Py.Repr((double)SmallBand / Kb)} -- the read assertions below rest on this item actually "
            + "being that size");
        var putB = await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = tableB,
            Item = Item(("pk", S(presentB)), ("pad", S(PadFor(LargeBand, presentB)))),
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        });
        var wcuB = putB.ConsumedCapacity.CapacityUnits;
        Ensure(
            wcuB == (double)LargeBand / Kb,
            $"the canary write of the {LargeBand}-byte item on table_b billed {Py.Repr(wcuB)} WCU, "
            + $"expected {Py.Repr((double)LargeBand / Kb)} -- the read assertions below rest on this item actually "
            + "being that size");

        var aBlocks = Math.Ceiling((double)SmallBand / RcuBlock);
        var bBlocks = Math.Ceiling((double)LargeBand / RcuBlock) + 1; // the present item's own blocks, plus one for the absent key

        foreach (var rcc in new[] { ReturnConsumedCapacity.TOTAL, ReturnConsumedCapacity.INDEXES })
        {
            foreach (var (consistent, factor) in new[] { (false, 0.5), (true, 1.0) })
            {
                var consistentRepr = consistent ? "True" : "False";
                var resp = await ddb.BatchGetItemAsync(new BatchGetItemRequest
                {
                    RequestItems = new()
                    {
                        [tableA] = new KeysAndAttributes { Keys = [Item(("pk", S(presentA)))], ConsistentRead = consistent },
                        [tableB] = new KeysAndAttributes { Keys = [Item(("pk", S(presentB))), Item(("pk", S(absentB)))], ConsistentRead = consistent },
                    },
                    ReturnConsumedCapacity = rcc,
                });
                var unprocessed = resp.UnprocessedKeys ?? [];
                Ensure(
                    unprocessed.Count == 0,
                    $"{rcc.Value} consistent={consistentRepr} BatchGetItem left UnprocessedKeys {Py.Repr(unprocessed.Keys)}; "
                    + "its capacity numbers therefore cover fewer than both tables' keys and cannot be compared");
                Ensure(
                    Py.Equal(resp.Responses[tableA], new[] { Item(("pk", S(presentA)), ("pad", S(PadFor(SmallBand, presentA)))) }),
                    $"table_a's Responses entry held {resp.Responses[tableA].Count} items, expected only the present item");
                Ensure(
                    Py.Equal(resp.Responses[tableB], new[] { Item(("pk", S(presentB)), ("pad", S(PadFor(LargeBand, presentB)))) }),
                    $"table_b's Responses entry held {resp.Responses[tableB].Count} items, expected only the "
                    + "present item -- the absent key must be omitted, never reported as unprocessed");

                var consumed = resp.ConsumedCapacity;
                var byTable = consumed.ToDictionary(entry => entry.TableName, StringComparer.Ordinal);
                Ensure(
                    consumed.Count == 2 && byTable.Keys.ToHashSet().SetEquals([tableA, tableB]),
                    $"{rcc.Value} consistent={consistentRepr} ConsumedCapacity must carry exactly one entry per "
                    + $"table in the request, got {Py.Repr(consumed.Select(c => (c.TableName, c.CapacityUnits)))}");

                var expectedA = aBlocks * factor;
                var expectedB = bBlocks * factor;
                var gotA = byTable[tableA].CapacityUnits;
                var gotB = byTable[tableB].CapacityUnits;
                Ensure(
                    gotA == expectedA,
                    $"{rcc.Value} consistent={consistentRepr}: table_a (one {SmallBand}-byte item) billed "
                    + $"{Py.Repr(gotA)}, expected {Py.Repr(expectedA)}");
                Ensure(
                    gotB == expectedB,
                    $"{rcc.Value} consistent={consistentRepr}: table_b (one {LargeBand}-byte item plus one "
                    + $"absent key) billed {Py.Repr(gotB)}, expected {Py.Repr(expectedB)} -- a key that misses must "
                    + "still round up and charge its own 4KB block, exactly like a key that hits");

                if (rcc == ReturnConsumedCapacity.INDEXES)
                {
                    var gotATable = byTable[tableA].Table?.CapacityUnits;
                    var gotBTable = byTable[tableB].Table?.CapacityUnits;
                    Ensure(
                        gotATable == gotA,
                        $"INDEXES table_a Table.CapacityUnits was {Py.Repr(gotATable)}, expected it to equal the entry's own total {Py.Repr(gotA)}");
                    Ensure(
                        gotBTable == gotB,
                        $"INDEXES table_b Table.CapacityUnits was {Py.Repr(gotBTable)}, expected it to equal the entry's own total {Py.Repr(gotB)}");
                }
            }
        }

        // Positive control: the absent key alone, read directly with GetItem, bills exactly the one
        // block that its contribution to the batch above assumed.
        var soloAbsent = await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = tableB,
            Key = Item(("pk", S(absentB))),
            ConsistentRead = true,
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        });
        Ensure(soloAbsent.Item is null, $"the absent key unexpectedly has an item: {Py.Repr(soloAbsent.Item)}");
        var soloCu = soloAbsent.ConsumedCapacity.CapacityUnits;
        Ensure(
            soloCu == 1.0,
            $"a strongly consistent GetItem on the absent key alone billed {Py.Repr(soloCu)}, expected 1.0 "
            + "(one full block, the same charge a miss costs when reached through BatchGetItem)");
    }

    /// <summary>Padding that lands the whole item just inside <paramref name="band"/> bytes.</summary>
    private static string PadFor(int band, string pk) => new('x', band - 2 - pk.Length - 3);

    private static async Task<string> SimpleTable(FidelityContext ctx, IAmazonDynamoDB ddb, string prefix)
    {
        var name = FidelityContext.UniqueName(prefix);
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 250);
        return name;
    }
}

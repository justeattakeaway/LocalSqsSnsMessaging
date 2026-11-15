// Translated from behavioral/dynamodb/check_batch_write_item_and_delete_item_index_capacity_arms_follow_item_estate.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-write-item-and-delete-item-index-capacity-arms-follow-item-estate</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchWriteItemAndDeleteItemIndexCapacityArmsFollowItemEstate
{
    /// <summary>
    /// With ReturnConsumedCapacity=INDEXES: a sparse DeleteItem bills only the table arm,
    /// BatchWriteItem reports one per-table entry summing its table and index arms, and an
    /// unchanged PutItem overwrite bills the table arm but no index arm.
    /// </summary>
    [Test]
    [KnownFidelityGap(FidelityGap.EmulatorBudget, "the check waits until time_left() - 60 s, which is no time at all under the runner's 60-second emulator budget, so its ACTIVE wait gives up before its first DescribeTable")]
    [DisplayName("dynamodb.batch-write-item-and-delete-item-index-capacity-arms-follow-item-estate")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbbwdl-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("gpk", ScalarAttributeType.S),
                new AttributeDefinition("lsk", ScalarAttributeType.N),
            ],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = "gsi1",
                    KeySchema = [new KeySchemaElement("gpk", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            LocalSecondaryIndexes =
            [
                new LocalSecondaryIndex
                {
                    IndexName = "lsi1",
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("lsk", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        var left = ctx.TimeLeftSeconds;
        var deadline = Math.Max(0.0, (left == 0 ? 900.0 : left) - 60.0);
        await WaitActive(ddb, table, FidelityTime.StartNew(), deadline);

        // --- DeleteItem of a sparse item (no GSI or LSI key present): table arm only ---
        await ddb.PutItemAsync(table, Item(("pk", S("sparse")), ("sk", S("1")), ("v", S("x"))));
        var resp = await ddb.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("sparse")), ("sk", S("1"))),
            ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES,
        });
        var cc = resp.ConsumedCapacity;
        var (tableU, gsiU, lsiU) = Breakdown(cc);
        Ensure(
            (tableU, gsiU, lsiU) == (1.0, 0.0, 0.0),
            $"deleting a sparse item (no index keys) billed table={Py.Repr(tableU)} gsi1={Py.Repr(gsiU)} " +
            $"lsi1={Py.Repr(lsiU)}, expected (1.0, 0.0, 0.0)");
        Ensure(
            cc.GlobalSecondaryIndexes is null && cc.LocalSecondaryIndexes is null,
            $"a sparse-item delete must carry no index breakdown members at all, got {Py.Repr(Boto(cc))}");
        Ensure(
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("sparse")), ("sk", S("1"))), ConsistentRead = true })).Item is null,
            "the deleted sparse item must be gone");

        // --- BatchWriteItem under ReturnConsumedCapacity=INDEXES: one entry per table, summed ---
        var batch = await ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new()
            {
                [table] =
                [
                    new WriteRequest { PutRequest = new PutRequest { Item = Item(("pk", S("b1")), ("sk", S("1")), ("gpk", S("g1")), ("lsk", N("1"))) } },
                    new WriteRequest { PutRequest = new PutRequest { Item = Item(("pk", S("b2")), ("sk", S("1")), ("gpk", S("g2")), ("lsk", N("2"))) } },
                ],
            },
            ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES,
        });
        var entries = batch.ConsumedCapacity;
        Ensure(
            entries is { Count: 1 },
            $"BatchWriteItem against one table must report exactly one entry, got {Py.Repr(entries?.Select(Boto))}");
        var entry = entries![0];
        Ensure(entry.TableName == table, $"the entry must name {Py.Repr(table)}, got {Py.Repr(entry.TableName)}");
        (tableU, gsiU, lsiU) = Breakdown(entry);
        Ensure(
            (tableU, gsiU, lsiU) == (2.0, 2.0, 2.0),
            $"two inserts into one GSI and one LSI billed table={Py.Repr(tableU)} gsi1={Py.Repr(gsiU)} " +
            $"lsi1={Py.Repr(lsiU)}, expected (2.0, 2.0, 2.0)");
        Ensure(
            entry.CapacityUnits == tableU + gsiU + lsiU,
            $"the per-table total {Py.Repr(entry.CapacityUnits)} must equal the sum of its arms " +
            $"{Py.Repr(tableU)} + {Py.Repr(gsiU)} + {Py.Repr(lsiU)}");
        foreach (var (pk, gpk, lsk) in new[] { ("b1", "g1", "1"), ("b2", "g2", "2") })
        {
            var item = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk)), ("sk", S("1"))), ConsistentRead = true })).Item;
            var want = Item(("pk", S(pk)), ("sk", S("1")), ("gpk", S(gpk)), ("lsk", N(lsk)));
            Ensure(Py.Equal(item, want), $"BatchWriteItem must have written {Py.Repr(want)}, got {Py.Repr(item)}");
        }

        // --- PutItem that rewrites an item with every value identical: no index arm ---
        Dictionary<string, AttributeValue> UnchangedItem() => Item(("pk", S("same")), ("sk", S("1")), ("gpk", S("g-same")), ("lsk", N("9")));
        var first = await ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = UnchangedItem(), ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES });
        var (firstTableU, firstGsiU, firstLsiU) = Breakdown(first.ConsumedCapacity);
        Ensure(
            (firstTableU, firstGsiU, firstLsiU) == (1.0, 1.0, 1.0),
            $"the first insert billed table={Py.Repr(firstTableU)} gsi1={Py.Repr(firstGsiU)} " +
            $"lsi1={Py.Repr(firstLsiU)}, expected (1.0, 1.0, 1.0)");

        var second = await ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = UnchangedItem(), ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES });
        var cc2 = second.ConsumedCapacity;
        (tableU, gsiU, lsiU) = Breakdown(cc2);
        Ensure(tableU == 1.0, $"an identical overwrite must still bill the table arm, got {Py.Repr(tableU)}");
        Ensure(
            (gsiU, lsiU) == (0.0, 0.0),
            "an identical overwrite changes no index entry and must bill no index arm, got " +
            $"gsi1={Py.Repr(gsiU)} lsi1={Py.Repr(lsiU)}");
        Ensure(
            cc2.GlobalSecondaryIndexes is null && cc2.LocalSecondaryIndexes is null,
            $"an identical overwrite must carry no index breakdown members at all, got {Py.Repr(Boto(cc2))}");
        var stillThere = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("same")), ("sk", S("1"))), ConsistentRead = true })).Item;
        Ensure(Py.Equal(stillThere, UnchangedItem()), $"the identical overwrite must leave the item exactly as written: {Py.Repr(stillThere)}");
    }

    /// <summary>A cleanup running undo() where a failed delete raises on anything but not_found.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };

    private static async Task WaitActive(IAmazonDynamoDB ddb, string table, FidelityStopwatch clock, double deadline)
    {
        while (clock.Elapsed.TotalSeconds < deadline)
        {
            var desc = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = table })).Table;
            if (desc.TableStatus == TableStatus.ACTIVE && (desc.GlobalSecondaryIndexes ?? []).All(i => i.IndexStatus == IndexStatus.ACTIVE))
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} did not reach ACTIVE with its GSI before the deadline");
    }

    /// <summary>(table_units, gsi1_units, lsi1_units) of an INDEXES ConsumedCapacity, 0 where absent.</summary>
    private static (double Table, double Gsi, double Lsi) Breakdown(ConsumedCapacity cc) => (
        cc.Table?.CapacityUnits ?? 0,
        cc.GlobalSecondaryIndexes?.GetValueOrDefault("gsi1")?.CapacityUnits ?? 0,
        cc.LocalSecondaryIndexes?.GetValueOrDefault("lsi1")?.CapacityUnits ?? 0);

    /// <summary>A ConsumedCapacity as boto3 hands it back: only the members present.</summary>
    private static Dictionary<string, object?> Boto(ConsumedCapacity cc)
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (cc.TableName is not null)
        {
            d["TableName"] = cc.TableName;
        }

        if (cc.CapacityUnits is not null)
        {
            d["CapacityUnits"] = cc.CapacityUnits;
        }

        if (cc.Table is not null)
        {
            d["Table"] = new Dictionary<string, object?> { ["CapacityUnits"] = cc.Table.CapacityUnits };
        }

        if (cc.GlobalSecondaryIndexes is not null)
        {
            d["GlobalSecondaryIndexes"] = cc.GlobalSecondaryIndexes.ToDictionary(kv => kv.Key, kv => (object?)new Dictionary<string, object?> { ["CapacityUnits"] = kv.Value.CapacityUnits }, StringComparer.Ordinal);
        }

        if (cc.LocalSecondaryIndexes is not null)
        {
            d["LocalSecondaryIndexes"] = cc.LocalSecondaryIndexes.ToDictionary(kv => kv.Key, kv => (object?)new Dictionary<string, object?> { ["CapacityUnits"] = kv.Value.CapacityUnits }, StringComparer.Ordinal);
        }

        return d;
    }
}

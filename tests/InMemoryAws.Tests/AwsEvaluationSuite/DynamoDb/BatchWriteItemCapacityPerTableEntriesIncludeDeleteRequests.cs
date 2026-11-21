// Translated from behavioral/dynamodb/check_batch_write_item_capacity_per_table_entries_include_delete_requests.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-write-item-capacity-per-table-entries-include-delete-requests</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchWriteItemCapacityPerTableEntriesIncludeDeleteRequests
{
    private const string Check = "batch-write-item-capacity-per-table-entries-include-delete-requests";

    /// <summary>
    /// A single BatchWriteItem spanning two tables returns one ConsumedCapacity entry per table
    /// under ReturnConsumedCapacity=INDEXES, each entry summing its PutRequest and DeleteRequest
    /// arms, with a DeleteRequest billed by the size of the item it removes.
    /// </summary>
    [Test]
    [KnownFidelityGap(FidelityGap.EmulatorBudget, "the check waits until time_left() - 60 s, which is no time at all under the runner's 60-second emulator budget, so its ACTIVE wait gives up before its first DescribeTable")]
    [DisplayName("dynamodb.batch-write-item-capacity-per-table-entries-include-delete-requests")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var left = ctx.TimeLeftSeconds;
        var clock = FidelityTime.StartNew();
        var deadline = Math.Max(0.0, (left == 0 ? 300.0 : left) - 60.0);

        var tableA = FidelityContext.UniqueName("sim2real-behav-ddbbw-a-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableA,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("gpk", ScalarAttributeType.S),
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
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(tableA), "ResourceNotFoundException"));

        var tableB = FidelityContext.UniqueName("sim2real-behav-ddbbw-b-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableB,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(tableB), "ResourceNotFoundException"));

        await WaitActive(ddb, tableA, clock, deadline);
        await WaitActive(ddb, tableB, clock, deadline);

        // Pre-seed the items each table's DeleteRequest will remove.
        var delKeyA = Item(("pk", S("a-del")), ("sk", S("1")));
        await ddb.PutItemAsync(tableA, Item(("pk", S("a-del")), ("sk", S("1")), ("gpk", S("g-del"))));
        var delItemB = ItemOfSize("b-del", "f", 2500);
        await ddb.PutItemAsync(tableB, delItemB);

        var putItemA = Item(("pk", S("a-put")), ("sk", S("1")), ("gpk", S("g-put")));
        var putItemB = ItemOfSize("b-put", "f", 1500);

        var resp = await ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new()
            {
                [tableA] =
                [
                    new WriteRequest { PutRequest = new PutRequest { Item = putItemA } },
                    new WriteRequest { DeleteRequest = new DeleteRequest { Key = delKeyA } },
                ],
                [tableB] =
                [
                    new WriteRequest { PutRequest = new PutRequest { Item = putItemB } },
                    new WriteRequest { DeleteRequest = new DeleteRequest { Key = Item(("pk", delItemB["pk"])) } },
                ],
            },
            ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES,
        });
        var unprocessed = resp.UnprocessedItems ?? [];
        Ensure(unprocessed.Count == 0, $"the four-request batch must be accepted whole, got UnprocessedItems={Py.Repr(unprocessed.Keys)}");

        var entries = resp.ConsumedCapacity;
        Ensure(
            entries is not null && entries.Count == 2,
            $"a BatchWriteItem spanning two tables must report one ConsumedCapacity entry per table, got {Py.Repr(entries?.Select(e => e.TableName))}");
        var byTable = entries!.ToDictionary(e => e.TableName, StringComparer.Ordinal);
        Ensure(
            byTable.Keys.ToHashSet().SetEquals([tableA, tableB]),
            $"the two entries must name {Py.Repr(tableA)} and {Py.Repr(tableB)}, got {Py.Repr(byTable.Keys.Order(StringComparer.Ordinal))}");

        var entryA = byTable[tableA];
        var (tableU, gsiU) = Breakdown(entryA);
        Ensure(
            (tableU, gsiU) == (2.0, 2.0),
            $"table_a's entry billed table={Py.Repr(tableU)} gsi1={Py.Repr(gsiU)} for one put and one delete of "
            + "gpk-bearing items, expected (2.0, 2.0): each of the put and the delete bills its own "
            + "table-arm unit and its own gsi1-arm unit");
        Ensure(
            entryA.LocalSecondaryIndexes is null,
            $"table_a has no LSI, so its entry must carry no LocalSecondaryIndexes member, got {Py.Repr(entryA.LocalSecondaryIndexes?.Keys)}");
        Ensure(
            entryA.CapacityUnits == tableU + gsiU,
            $"table_a's total {Py.Repr(entryA.CapacityUnits)} must equal its table arm {Py.Repr(tableU)} plus its gsi1 arm {Py.Repr(gsiU)}");

        var entryB = byTable[tableB];
        var (tableUB, gsiUB) = Breakdown(entryB);
        Ensure(
            gsiUB == 0.0 && entryB.GlobalSecondaryIndexes is null,
            $"table_b has no GSI, so its entry must carry no GlobalSecondaryIndexes member, got {Py.Repr(entryB.GlobalSecondaryIndexes?.Keys)}");
        Ensure(
            tableUB == 5.0,
            $"table_b's table arm billed {Py.Repr(tableUB)} for a 1500-byte put (rounds to 2) and a "
            + "2500-byte delete (rounds to 3), expected 5.0: a DeleteRequest is billed by the size of "
            + "the item it removes, not a flat unit");
        Ensure(
            entryB.CapacityUnits == tableUB,
            $"table_b's total {Py.Repr(entryB.CapacityUnits)} must equal its table arm {Py.Repr(tableUB)} when it carries no index");

        var gotPutA = await Get(ddb, tableA, Item(("pk", S("a-put")), ("sk", S("1"))));
        Ensure(Py.Equal(gotPutA, putItemA), $"the put into table_a must have landed, got {Py.Repr(gotPutA)}");
        Ensure(await Get(ddb, tableA, delKeyA) is null, "the deleted item in table_a must be gone");

        var gotPutB = await Get(ddb, tableB, Item(("pk", S("b-put"))));
        Ensure(
            Py.Equal(gotPutB, putItemB),
            $"the 1500-byte batched put into table_b must round-trip whole, got {(gotPutB is null ? "None" : (gotPutB.GetValueOrDefault("f")?.S?.Length ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture))} bytes back");
        Ensure(await Get(ddb, tableB, Item(("pk", delItemB["pk"]))) is null, "the deleted 2500-byte item in table_b must be gone");
    }

    /// <summary>A one-pk, one-filler-attribute item whose aggregate encoded size is exactly totalBytes.</summary>
    private static Dictionary<string, AttributeValue> ItemOfSize(string pkValue, string fillerAttr, int totalBytes)
    {
        var overhead = "pk".Length + pkValue.Length + fillerAttr.Length;
        var fillerLen = totalBytes - overhead;
        Ensure(fillerLen > 0, $"{totalBytes} bytes is too small for this item shape");
        return Item(("pk", S(pkValue)), (fillerAttr, S(new string('x', fillerLen))));
    }

    /// <summary>(table_units, gsi1_units) of an INDEXES ConsumedCapacity entry, 0 where absent.</summary>
    private static (double Table, double Gsi) Breakdown(ConsumedCapacity cc) => (
        cc.Table?.CapacityUnits ?? 0,
        cc.GlobalSecondaryIndexes?.GetValueOrDefault("gsi1")?.CapacityUnits ?? 0);

    private static async Task<Dictionary<string, AttributeValue>?> Get(IAmazonDynamoDB ddb, string table, Dictionary<string, AttributeValue> key) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;

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

        throw Fail($"table {table} did not reach ACTIVE before the deadline");
    }

    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };
}

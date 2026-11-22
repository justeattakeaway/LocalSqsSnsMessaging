// Translated from behavioral/dynamodb/check_batch_write_item_index_capacity_arms_mixed_sparse_lsi_item.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-write-item-index-capacity-arms-mixed-sparse-lsi-item</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchWriteItemIndexCapacityArmsMixedSparseLsiItem
{
    /// <summary>
    /// With ReturnConsumedCapacity=INDEXES, one BatchWriteItem writing a fully-indexed item
    /// and a second item missing only the LSI key reports a single per-table entry whose GSI
    /// arm sums both items and whose LSI arm reflects only the one that carried it.
    /// </summary>
    [Test]
    [KnownFidelityGap(FidelityGap.EmulatorBudget, "the check waits until time_left() - 60 s, which is no time at all under the runner's 60-second emulator budget, so its ACTIVE wait gives up before its first DescribeTable")]
    [DisplayName("dynamodb.batch-write-item-index-capacity-arms-mixed-sparse-lsi-item")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbmix-");
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
        var clock = FidelityTime.StartNew();
        var deadline = Math.Max(0.0, (left == 0 ? 900.0 : left) - 60.0);
        await WaitActive(ddb, table, clock, deadline);

        var fullItem = Item(("pk", S("full")), ("sk", S("1")), ("gpk", S("g-full")), ("lsk", N("1")));
        var sparseLsiItem = Item(("pk", S("sparse-lsi")), ("sk", S("1")), ("gpk", S("g-sparse")));
        var resp = await ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new()
            {
                [table] =
                [
                    new WriteRequest { PutRequest = new PutRequest { Item = fullItem } },
                    new WriteRequest { PutRequest = new PutRequest { Item = sparseLsiItem } },
                ],
            },
            ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES,
        });
        var entries = resp.ConsumedCapacity;
        Ensure(
            entries is not null && entries.Count == 1,
            $"BatchWriteItem against one table must report exactly one entry, got {Py.Repr(entries?.Select(e => e.TableName))}");
        var entry = entries![0];
        Ensure(entry.TableName == table, $"the entry must name {Py.Repr(table)}, got {Py.Repr(entry.TableName)}");
        var (tableU, gsiU, lsiU) = Breakdown(entry);
        Ensure(
            (tableU, gsiU, lsiU) == (2.0, 2.0, 1.0),
            "one item carrying every index key and one carrying only the GSI key billed "
            + $"table={Py.Repr(tableU)} gsi1={Py.Repr(gsiU)} lsi1={Py.Repr(lsiU)}, expected (2.0, 2.0, 1.0)");
        Ensure(
            entry.CapacityUnits == tableU + gsiU + lsiU,
            $"the per-table total {Py.Repr(entry.CapacityUnits)} must equal the sum of its arms "
            + $"{Py.Repr(tableU)} + {Py.Repr(gsiU)} + {Py.Repr(lsiU)}");

        var gotFull = await Get(ddb, table, "full");
        Ensure(Py.Equal(gotFull, fullItem), $"the fully-indexed item must round-trip exactly, got {Py.Repr(gotFull)}");
        var gotSparse = await Get(ddb, table, "sparse-lsi");
        Ensure(Py.Equal(gotSparse, sparseLsiItem), $"the LSI-sparse item must round-trip with no lsk attribute, got {Py.Repr(gotSparse)}");
    }

    /// <summary>(table_units, gsi1_units, lsi1_units) of an INDEXES ConsumedCapacity, 0 where absent.</summary>
    private static (double Table, double Gsi, double Lsi) Breakdown(ConsumedCapacity cc) => (
        cc.Table?.CapacityUnits ?? 0,
        cc.GlobalSecondaryIndexes?.GetValueOrDefault("gsi1")?.CapacityUnits ?? 0,
        cc.LocalSecondaryIndexes?.GetValueOrDefault("lsi1")?.CapacityUnits ?? 0);

    private static async Task<Dictionary<string, AttributeValue>?> Get(IAmazonDynamoDB ddb, string table, string pk) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk)), ("sk", S("1"))), ConsistentRead = true })).Item;

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

    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };
}

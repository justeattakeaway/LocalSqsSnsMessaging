// Translated from behavioral/dynamodb/check_update_item_gsi_key_move_recharges_an_all_projected_lsi.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-gsi-key-move-recharges-an-all-projected-lsi</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemGsiKeyMoveRechargesAnAllProjectedLsi
{
    /// <summary>A cleanup running undo() where a failed delete raises on anything but not_found.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };

    private static async Task WaitActive(IAmazonDynamoDB ddb, string table, FidelityStopwatch clock, double boundSeconds)
    {
        TableDescription? desc = null;
        while (clock.Elapsed.TotalSeconds < boundSeconds)
        {
            desc = (await ddb.DescribeTableAsync(table)).Table;
            if (desc.TableStatus == TableStatus.ACTIVE && (desc.GlobalSecondaryIndexes ?? []).All(i => i.IndexStatus == IndexStatus.ACTIVE))
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} did not reach ACTIVE with its GSI before the deadline: {Py.Repr(desc)}");
    }

    /// <summary>(table_units, gsi1_units, lsi1_units) of an INDEXES ConsumedCapacity, 0 where absent.</summary>
    private static (double Table, double Gsi, double Lsi) Breakdown(UpdateItemResponse resp)
    {
        var cc = resp.ConsumedCapacity;
        var tableUnits = cc?.Table?.CapacityUnits ?? 0;
        var gsiUnits = cc?.GlobalSecondaryIndexes?.GetValueOrDefault("gsi1")?.CapacityUnits ?? 0;
        var lsiUnits = cc?.LocalSecondaryIndexes?.GetValueOrDefault("lsi1")?.CapacityUnits ?? 0;
        return (tableUnits, gsiUnits, lsiUnits);
    }

    private static async Task<T> Poll<T>(Func<Task<T>> fn, Func<T, bool> pred, FidelityStopwatch clock, double boundSeconds)
    {
        var result = await fn();
        while (!pred(result) && clock.Elapsed.TotalSeconds < boundSeconds)
        {
            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
            result = await fn();
        }

        return result;
    }

    /// <summary>
    /// UpdateItem moving a GSI's partition-key attribute bills the GSI 2 (delete plus put) and,
    /// when the same attribute is projected ALL into an unrelated LSI whose own key is untouched,
    /// also bills that LSI 1 for re-storing its entry with the new value: table 1 + gsi 2 + lsi 1 = 4.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-gsi-key-move-recharges-an-all-projected-lsi")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EmulatorBudget, "The check's deadline is the runner's time left minus 60 s, which is nothing under the 60-second emulator budget, so the ACTIVE wait gives up at once")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbgla-");
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
                    Projection = new Projection { ProjectionType = ProjectionType.KEYS_ONLY },
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
        var bound = Math.Max(0.0, (left == 0 ? 900.0 : left) - 60.0);
        var clock = FidelityTime.StartNew();
        await WaitActive(ddb, table, clock, bound);

        var key = Item(("pk", S("a")), ("sk", S("1")));
        await ddb.PutItemAsync(table, Item(("pk", S("a")), ("sk", S("1")), ("gpk", S("g-old")), ("lsk", N("7"))));

        Task<QueryResponse> GsiQuery(string gpk) => ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = "gsi1",
            KeyConditionExpression = "gpk = :v",
            ExpressionAttributeValues = Item((":v", S(gpk))),
        });

        Task<QueryResponse> LsiQuery() => ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = "lsi1",
            ConsistentRead = true,
            KeyConditionExpression = "pk = :p AND lsk = :v",
            ExpressionAttributeValues = Item((":p", S("a")), (":v", N("7"))),
        });

        var present = await Poll(() => GsiQuery("g-old"), r => r.Count == 1, clock, bound);
        Ensure(present.Count == 1, $"item never appeared under gpk=g-old before the update: {Py.Repr(present.Items)}");

        var beforeLsi = await LsiQuery();
        Ensure(
            Py.Equal(beforeLsi.Items, new[] { Item(("pk", S("a")), ("sk", S("1")), ("gpk", S("g-old")), ("lsk", N("7"))) }),
            $"the all-projected LSI entry before the update was {Py.Repr(beforeLsi.Items)}");

        var resp = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET gpk = :new",
            ExpressionAttributeValues = Item((":new", S("g-new"))),
            ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES,
        });
        var (tableU, gsiU, lsiU) = Breakdown(resp);
        Ensure(
            (tableU, gsiU, lsiU) == (1.0, 2.0, 1.0),
            "moving the GSI key while an unrelated ALL-projected LSI key stayed put billed " +
            $"table={Py.Repr(tableU)} gsi1={Py.Repr(gsiU)} lsi1={Py.Repr(lsiU)}, expected (1.0, 2.0, 1.0)");
        var cc = resp.ConsumedCapacity;
        Ensure(cc.CapacityUnits == 4.0, $"the aggregate CapacityUnits was {Py.Repr(cc.CapacityUnits)}, expected 4.0");

        var moved = await Poll(
            async () => ((await GsiQuery("g-new")).Items, (await GsiQuery("g-old")).Items),
            pair => pair.Item1.Count > 0 && pair.Item2.Count == 0,
            clock,
            bound);
        var (newItems, oldItems) = moved;
        Ensure(
            newItems.Select(i => i["pk"].S).SequenceEqual(["a"]),
            $"item never appeared under gpk=g-new: {Py.Repr(newItems)}");
        Ensure(oldItems.Count == 0, $"the stale gsi1 entry under gpk=g-old survived the key update: {Py.Repr(oldItems)}");

        var afterLsi = await LsiQuery();
        Ensure(
            Py.Equal(afterLsi.Items, new[] { Item(("pk", S("a")), ("sk", S("1")), ("gpk", S("g-new")), ("lsk", N("7"))) }),
            "the all-projected LSI entry, whose own key never moved, must still have been re-stored " +
            $"with the new gpk value: {Py.Repr(afterLsi.Items)}");
    }
}

// Translated from behavioral/dynamodb/check_update_item_index_capacity_ladder_follows_key_vs_projected_change.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-index-capacity-ladder-follows-key-vs-projected-change</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemIndexCapacityLadderFollowsKeyVsProjectedChange
{
    /// <summary>
    /// UpdateItem's index write capacity, with ReturnConsumedCapacity=INDEXES, charges 2 units for
    /// an index whose key attribute changed value (a delete of the old entry plus a put of the new
    /// one) and 1 unit for an index that keeps its key but re-stores a changed projected attribute.
    /// </summary>
    [Test]
    [KnownFidelityGap(FidelityGap.EmulatorBudget, "The check's deadline is the runner's time left minus 60 s, which is nothing under the 60-second emulator budget, so the ACTIVE wait gives up at once")]
    [DisplayName("dynamodb.update-item-index-capacity-ladder-follows-key-vs-projected-change")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbupl-");
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
                    Projection = new Projection { ProjectionType = ProjectionType.INCLUDE, NonKeyAttributes = ["proj"] },
                },
            ],
            LocalSecondaryIndexes =
            [
                new LocalSecondaryIndex
                {
                    IndexName = "lsi1",
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("lsk", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.INCLUDE, NonKeyAttributes = ["proj"] },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(async () =>
        {
            try
            {
                await ddb.DeleteTableAsync(table);
            }
            catch (ResourceNotFoundException)
            {
            }
        });
        var timeLeft = ctx.TimeLeftSeconds;
        var budget = TimeSpan.FromSeconds(Math.Max(0.0, (timeLeft != 0 ? timeLeft : 900.0) - 60.0));
        var clock = FidelityTime.StartNew();
        bool BeforeDeadline() => clock.Elapsed < budget;
        await WaitActive(ddb, table, BeforeDeadline);

        Task<QueryResponse> QueryGsi(string gpk) => ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = "gsi1",
            KeyConditionExpression = "gpk = :v",
            ExpressionAttributeValues = Item((":v", S(gpk))),
        });
        Task<QueryResponse> QueryLsi(string pk, string lsk) => ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = "lsi1",
            KeyConditionExpression = "pk = :p AND lsk = :v",
            ExpressionAttributeValues = Item((":p", S(pk)), (":v", N(lsk))),
        });

        // --- arm A: an UpdateItem that changes the GSI partition-key attribute ---
        await ddb.PutItemAsync(table, Item(
            ("pk", S("a")), ("sk", S("1")), ("gpk", S("g-old")), ("lsk", N("1")), ("proj", S("p0"))));
        var present = await PollQuery(() => QueryGsi("g-old"), r => r.Count == 1, BeforeDeadline);
        Ensure(present.Count == 1, $"item never appeared under gpk=g-old before the update: {Py.Repr(present.Items)}");

        var resp = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("a")), ("sk", S("1"))),
            UpdateExpression = "SET gpk = :new",
            ExpressionAttributeValues = Item((":new", S("g-new"))),
            ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES,
        });
        var (tableU, gsiU, lsiU) = Breakdown(resp);
        Ensure(
            (tableU, gsiU, lsiU) == (1.0, 2.0, 0.0),
            $"changing a GSI partition-key attribute billed table={Py.Repr(tableU)} gsi1={Py.Repr(gsiU)} " +
            $"lsi1={Py.Repr(lsiU)}, expected (1.0, 2.0, 0.0)");

        var (newItems, oldItems) = await PollQuery(
            async () => ((await QueryGsi("g-new")).Items, (await QueryGsi("g-old")).Items),
            pair => pair.Item1.Count > 0 && pair.Item2.Count == 0,
            BeforeDeadline);
        Ensure(newItems.Select(i => i["pk"].S).SequenceEqual(["a"]), $"item never appeared under gpk=g-new: {Py.Repr(newItems)}");
        Ensure(oldItems.Count == 0, $"the stale gsi1 entry under gpk=g-old survived the key update: {Py.Repr(oldItems)}");

        // --- arm B: an UpdateItem that changes the LSI sort-key attribute ---
        await ddb.PutItemAsync(table, Item(
            ("pk", S("b")), ("sk", S("1")), ("gpk", S("g-b")), ("lsk", N("5")), ("proj", S("p0"))));
        present = await PollQuery(() => QueryLsi("b", "5"), r => r.Count == 1, BeforeDeadline);
        Ensure(present.Count == 1, $"item never appeared under lsk=5 before the update: {Py.Repr(present.Items)}");

        resp = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("b")), ("sk", S("1"))),
            UpdateExpression = "SET lsk = :new",
            ExpressionAttributeValues = Item((":new", N("6"))),
            ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES,
        });
        (tableU, gsiU, lsiU) = Breakdown(resp);
        Ensure(
            (tableU, gsiU, lsiU) == (1.0, 0.0, 2.0),
            $"changing an LSI sort-key attribute billed table={Py.Repr(tableU)} gsi1={Py.Repr(gsiU)} " +
            $"lsi1={Py.Repr(lsiU)}, expected (1.0, 0.0, 2.0)");

        (newItems, oldItems) = await PollQuery(
            async () => ((await QueryLsi("b", "6")).Items, (await QueryLsi("b", "5")).Items),
            pair => pair.Item1.Count > 0 && pair.Item2.Count == 0,
            BeforeDeadline);
        Ensure(newItems.Select(i => i["pk"].S).SequenceEqual(["b"]), $"item never appeared under lsk=6: {Py.Repr(newItems)}");
        Ensure(oldItems.Count == 0, $"the stale lsi1 entry under lsk=5 survived the key update: {Py.Repr(oldItems)}");

        // --- arm C: an UpdateItem that changes only a projected, non-key attribute ---
        await ddb.PutItemAsync(table, Item(
            ("pk", S("c")), ("sk", S("1")), ("gpk", S("g-c")), ("lsk", N("9")), ("proj", S("p0"))));
        present = await PollQuery(() => QueryGsi("g-c"), r => r.Count == 1, BeforeDeadline);
        Ensure(present.Count == 1, $"item never appeared under gpk=g-c before the update: {Py.Repr(present.Items)}");

        resp = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("c")), ("sk", S("1"))),
            UpdateExpression = "SET proj = :new",
            ExpressionAttributeValues = Item((":new", S("p1"))),
            ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES,
        });
        (tableU, gsiU, lsiU) = Breakdown(resp);
        Ensure(
            (tableU, gsiU, lsiU) == (1.0, 1.0, 1.0),
            $"changing only a projected, non-key attribute billed table={Py.Repr(tableU)} gsi1={Py.Repr(gsiU)} " +
            $"lsi1={Py.Repr(lsiU)}, expected (1.0, 1.0, 1.0)");

        var viaGsi = await PollQuery(
            () => QueryGsi("g-c"),
            r => r.Items.Count > 0 && r.Items[0].GetValueOrDefault("proj")?.S == "p1",
            BeforeDeadline);
        Ensure(viaGsi.Items[0]["proj"].S == "p1", $"the INCLUDE-projection gsi1 entry did not pick up the updated attribute: {Py.Repr(viaGsi.Items)}");
        var viaLsi = await QueryLsi("c", "9");
        Ensure(viaLsi.Items[0]["proj"].S == "p1", $"the INCLUDE-projection lsi1 entry did not pick up the updated attribute: {Py.Repr(viaLsi.Items)}");
    }

    private static async Task WaitActive(IAmazonDynamoDB ddb, string table, Func<bool> beforeDeadline)
    {
        while (beforeDeadline())
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
    private static (double Table, double Gsi, double Lsi) Breakdown(UpdateItemResponse resp)
    {
        var cc = resp.ConsumedCapacity;
        var tableUnits = cc?.Table?.CapacityUnits ?? 0;
        var gsiUnits = cc?.GlobalSecondaryIndexes?.GetValueOrDefault("gsi1")?.CapacityUnits ?? 0;
        var lsiUnits = cc?.LocalSecondaryIndexes?.GetValueOrDefault("lsi1")?.CapacityUnits ?? 0;
        return (tableUnits, gsiUnits, lsiUnits);
    }

    private static async Task<T> PollQuery<T>(Func<Task<T>> query, Func<T, bool> pred, Func<bool> beforeDeadline)
    {
        var result = await query();
        while (!pred(result) && beforeDeadline())
        {
            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
            result = await query();
        }

        return result;
    }
}

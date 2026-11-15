// Translated from behavioral/dynamodb/check_write_capacity_per_gsi_counts_index_inserts_removals_and_projected_size.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.write-capacity-per-gsi-counts-index-inserts-removals-and-projected-size</summary>
[Category("AwsEvaluationSuite")]
public sealed class WriteCapacityPerGsiCountsIndexInsertsRemovalsAndProjectedSize
{
    /// <summary>
    /// Write capacity counts one unit per GSI entry written or removed, sized by its projection; TOTAL
    /// sums base and indexes without a breakdown, and a write touching no projected attribute bills none.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.write-capacity-per-gsi-counts-index-inserts-removals-and-projected-size")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var two = FidelityContext.UniqueName("sim2real-behav-ddb-gsicu-");
        var ko = $"{two}-ko";
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = two,
            AttributeDefinitions = [.. new[] { "pk", "g1", "g2" }.Select(n => new AttributeDefinition(n, ScalarAttributeType.S))],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            GlobalSecondaryIndexes = [Gsi("gsi1", "g1", ProjectionType.ALL), Gsi("gsi2", "g2", ProjectionType.ALL)],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(two), "ResourceNotFoundException"));
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = ko,
            AttributeDefinitions = [.. new[] { "pk", "g1" }.Select(n => new AttributeDefinition(n, ScalarAttributeType.S))],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            GlobalSecondaryIndexes = [Gsi("gsi_pk", "pk", ProjectionType.KEYS_ONLY), Gsi("gsi_ko", "g1", ProjectionType.KEYS_ONLY)],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(ko), "ResourceNotFoundException"));
        await WaitActive(ddb, ctx, two);
        await WaitActive(ddb, ctx, ko);

        static Dictionary<string, AttributeValue> WithBoth(string pk) => Item(("pk", S(pk)), ("g1", S("x")), ("g2", S("y")));
        var resp = await ddb.PutItemAsync(new PutItemRequest { TableName = two, Item = WithBoth("c"), ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL });
        var cc = resp.ConsumedCapacity;
        Ensure(cc?.CapacityUnits == 3.0, $"TOTAL for a write into both GSIs is {Repr(cc)}, expected 3.0");
        Ensure(cc.Table is null && cc.GlobalSecondaryIndexes is null, $"TOTAL carries a breakdown: {Repr(cc)}");

        var got = Breakdown((await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = two,
            Key = Item(("pk", S("e"))),
            UpdateExpression = "SET g1 = :a, g2 = :b",
            ExpressionAttributeValues = Item((":a", S("x")), (":b", S("y"))),
            ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES,
        })).ConsumedCapacity);
        Ensure(Py.Equal(got, (3.0, 1.0, Units(("gsi1", 1.0), ("gsi2", 1.0)))), $"UpdateItem setting both GSI keys billed {Py.Repr(got)}");

        await ddb.PutItemAsync(new PutItemRequest { TableName = two, Item = WithBoth("ow") });
        got = Breakdown((await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = two,
            Item = Item(("pk", S("ow")), ("g1", S("x"))),
            ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES,
        })).ConsumedCapacity);
        Ensure(Py.Equal(got, (3.0, 1.0, Units(("gsi1", 1.0), ("gsi2", 1.0)))), $"an overwrite dropping g2 billed {Py.Repr(got)}");

        await ddb.PutItemAsync(new PutItemRequest { TableName = two, Item = WithBoth("rm") });
        var (total, @base, gsis) = Breakdown((await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = two,
            Key = Item(("pk", S("rm"))),
            UpdateExpression = "REMOVE g2",
            ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES,
        })).ConsumedCapacity);
        Ensure(
            Py.Equal((total, @base, gsis), (3.0, 1.0, Units(("gsi1", 1.0), ("gsi2", 1.0)))),
            $"REMOVE g2 billed total {Py.Repr(total)}, table {Py.Repr(@base)}, GSIs {Py.Repr(gsis)}");
        Ensure(total == @base + gsis.Values.Sum(), $"REMOVE g2 billed total {Py.Repr(total)} for table {Py.Repr(@base)} + {Py.Repr(gsis)}");

        var left = ctx.TimeLeftSeconds;
        var deadline = TimeSpan.FromSeconds(Math.Max(0.0, (left != 0 ? left : 120.0) - 20.0));
        var clock = FidelityTime.StartNew();

        async Task<List<string>> InGsi2()
        {
            var pks = new List<string>();
            Dictionary<string, AttributeValue>? startKey = null;
            do
            {
                var page = await ddb.ScanAsync(new ScanRequest { TableName = two, IndexName = "gsi2", ExclusiveStartKey = startKey });
                pks.AddRange((page.Items ?? []).Select(i => i["pk"].S));
                startKey = page.LastEvaluatedKey is { Count: > 0 } next ? next : null;
            }
            while (startKey is not null);

            return [.. pks.Order(StringComparer.Ordinal)];
        }

        List<string> held;
        while (true)
        {
            held = await InGsi2();
            if (held.SequenceEqual(["c", "e"]) || clock.Elapsed >= deadline)
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        Ensure(held.SequenceEqual(["c", "e"]), $"gsi2 holds {Py.Repr(held)}; the items whose g2 was dropped must have left it");

        got = Breakdown((await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = ko,
            Key = Item(("pk", S("up"))),
            UpdateExpression = "SET #o = :v",
            ExpressionAttributeNames = Names(("#o", "other")),
            ExpressionAttributeValues = Item((":v", S("created"))),
            ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES,
        })).ConsumedCapacity);
        Ensure(Py.Equal(got, (2.0, 1.0, Units(("gsi_pk", 1.0)))), $"an upsert on a table whose GSI reuses the base key billed {Py.Repr(got)}");

        got = Breakdown((await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = ko,
            Item = Item(("pk", S("a")), ("g1", S("gv")), ("big", S(new string('x', 2000)))),
            ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES,
        })).ConsumedCapacity);
        Ensure(Py.Equal(got, (4.0, 2.0, Units(("gsi_pk", 1.0), ("gsi_ko", 1.0)))), $"a 2 KB item into two KEYS_ONLY GSIs billed {Py.Repr(got)}");

        got = Breakdown((await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = ko,
            Key = Item(("pk", S("a"))),
            UpdateExpression = "SET big = :v",
            ExpressionAttributeValues = Item((":v", S(new string('y', 2000)))),
            ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES,
        })).ConsumedCapacity);
        Ensure(Py.Equal(got, (2.0, 2.0, Units())), $"an update of a non-projected attribute billed {Py.Repr(got)}");
    }

    private static GlobalSecondaryIndex Gsi(string name, string attr, ProjectionType projection) => new()
    {
        IndexName = name,
        KeySchema = [new KeySchemaElement(attr, KeyType.HASH)],
        Projection = new Projection { ProjectionType = projection },
    };

    private static Dictionary<string, double?> Units(params (string Name, double Units)[] entries) =>
        entries.ToDictionary(e => e.Name, e => (double?)e.Units, StringComparer.Ordinal);

    /// <summary>(total, table, {gsi: units}) of an INDEXES ConsumedCapacity.</summary>
    private static (double? Total, double? Table, Dictionary<string, double?> Gsis) Breakdown(ConsumedCapacity? cc)
    {
        var gsis = (cc?.GlobalSecondaryIndexes ?? []).ToDictionary(e => e.Key, e => e.Value.CapacityUnits, StringComparer.Ordinal);
        return (cc?.CapacityUnits, cc?.Table?.CapacityUnits, gsis);
    }

    private static string Repr(ConsumedCapacity? cc) =>
        cc is null
            ? "{}"
            : $"{{'TableName': {Py.Repr(cc.TableName)}, 'CapacityUnits': {Py.Repr(cc.CapacityUnits)}"
                + (cc.Table is null ? "" : $", 'Table': {{'CapacityUnits': {Py.Repr(cc.Table.CapacityUnits)}}}")
                + (cc.GlobalSecondaryIndexes is null ? "" : $", 'GlobalSecondaryIndexes': {Py.Repr(cc.GlobalSecondaryIndexes.ToDictionary(e => e.Key, e => e.Value.CapacityUnits))}")
                + "}";

    /// <summary>A cleanup running undo() where a failed delete raises; in-use is retried every 2 s.</summary>
    private static Func<Task> Teardown(Func<Task> undo, string notFound, double timeoutS = 300.0) => async () =>
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(undo);
            if (error is null || error.Code == notFound)
            {
                return;
            }

            if (error.Code != "ResourceInUseException" || clock.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    };

    /// <summary>Poll DescribeTable every 2 s until the table and its indexes are ACTIVE.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var left = ctx.TimeLeftSeconds;
        var deadline = TimeSpan.FromSeconds(Math.Max(0.0, (left != 0 ? left : 900.0) - 30.0));
        var clock = FidelityTime.StartNew();
        TableDescription? desc = null;
        while (clock.Elapsed < deadline)
        {
            desc = (await ddb.DescribeTableAsync(table)).Table;
            if (desc.TableStatus == TableStatus.ACTIVE && (desc.GlobalSecondaryIndexes ?? []).All(g => g.IndexStatus == IndexStatus.ACTIVE))
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {desc?.TableStatus?.Value}");
    }
}

// Translated from behavioral/dynamodb/check_query_gsi_four_range_key_schema_orders_narrows_and_reverses.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-gsi-four-range-key-schema-orders-narrows-and-reverses</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryGsiFourRangeKeySchemaOrdersNarrowsAndReverses
{
    private const string Check = "query-gsi-four-range-key-schema-orders-narrows-and-reverses";
    private const string Index = "by-four-range";
    private static readonly string[] Range = ["gsk1", "gsk2", "gsk3", "gsk4"];

    // (pk, gsk1, gsk2, gsk3, gsk4); every RANGE position differs across the set, and p1/p2 share
    // every attribute but gsk4 so a prefix can narrow all the way down to a two-item range.
    private static readonly (string Pk, string G1, string G2, string G3, string G4)[] Items =
    [
        ("p1", "eng", "api", "ic", "2023"),
        ("p2", "eng", "api", "ic", "2024"),
        ("p3", "eng", "api", "mgr", "2023"),
        ("p4", "eng", "web", "ic", "2023"),
        ("p5", "eng", "web", "mgr", "2024"),
        ("p6", "ops", "api", "ic", "2023"),
        ("p7", "ops", "api", "mgr", "2023"),
        ("p8", "ops", "web", "mgr", "2024"),
    ];

    /// <summary>read() every 2 s until it equals want, bounded by the run budget.</summary>
    private static async Task<List<string>> Poll(FidelityContext ctx, Func<Task<List<string>>> read, List<string> want)
    {
        var left = ctx.TimeLeftSeconds;
        var clock = FidelityTime.StartNew();
        var deadline = TimeSpan.FromSeconds(Math.Max(0.0, (left == 0 ? 120.0 : left) - 20.0));
        while (true)
        {
            var value = await read();
            if (value.SequenceEqual(want) || clock.Elapsed >= deadline)
            {
                return value;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>Poll DescribeTable every 2 s until the table and its indexes are ACTIVE.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var left = ctx.TimeLeftSeconds;
        var clock = FidelityTime.StartNew();
        var deadline = TimeSpan.FromSeconds(Math.Max(0.0, (left == 0 ? 300.0 : left) - 30.0));
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

        throw Fail($"table {table} not ACTIVE before the deadline: {Py.Repr(desc)}");
    }

    private static List<Dictionary<string, string>> Boto(IEnumerable<KeySchemaElement> schema) =>
        [.. schema.Select(k => new Dictionary<string, string>(StringComparer.Ordinal) { ["AttributeName"] = k.AttributeName, ["KeyType"] = k.KeyType.Value })];

    /// <summary>
    /// A GSI keyed on one HASH and four RANGE attributes orders a HASH-only Query by the RANGE
    /// attributes in schema order (reversed under ScanIndexForward=false), narrows under a 1-3
    /// attribute equality prefix or a range condition right after one, free of clause order.
    /// </summary>
    [Test]
    [KnownFidelityGap(FidelityGap.DynamoDbMultiAttributeKeys, "CreateTable refuses a GSI key schema with four RANGE attributes")]
    [DisplayName("dynamodb.query-gsi-four-range-key-schema-orders-narrows-and-reverses")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-gsi4r-");
        List<KeySchemaElement> keySchema = [new("gpk", KeyType.HASH), .. Range.Select(n => new KeySchemaElement(n, KeyType.RANGE))];
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            AttributeDefinitions = [.. new[] { "pk", "gpk" }.Concat(Range).Select(n => new AttributeDefinition(n, ScalarAttributeType.S))],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = Index,
                    KeySchema = keySchema,
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        var desc = await WaitActive(ddb, ctx, table);
        var described = Boto(desc.GlobalSecondaryIndexes[0].KeySchema);
        Ensure(Py.Equal(described, Boto(keySchema)), $"the GSI describes its key as {Py.Repr(described)}");

        foreach (var (pk, g1, g2, g3, g4) in Items)
        {
            await ddb.PutItemAsync(table, Item(
                ("pk", S(pk)), ("gpk", S("g1")),
                ("gsk1", S(g1)), ("gsk2", S(g2)), ("gsk3", S(g3)), ("gsk4", S(g4))));
        }

        async Task<List<string>> Query(string cond, Dictionary<string, AttributeValue> values, bool forward = true)
        {
            var items = new List<Dictionary<string, AttributeValue>>();
            Dictionary<string, AttributeValue>? start = null;
            do
            {
                var page = await ddb.QueryAsync(new QueryRequest
                {
                    TableName = table,
                    IndexName = Index,
                    KeyConditionExpression = cond,
                    ExpressionAttributeValues = values,
                    ScanIndexForward = forward,
                    ExclusiveStartKey = start,
                });
                items.AddRange(page.Items);
                start = page.LastEvaluatedKey;
            }
            while (start is { Count: > 0 });

            return [.. items.Select(i => i["pk"].S)];
        }

        List<string> allPks = [.. Items.Select(row => row.Pk)];

        // A HASH-only query orders by the RANGE attributes in schema order (gsk1, gsk2, gsk3, gsk4).
        var got = await Poll(ctx, () => Query("gpk = :g", Item((":g", S("g1")))), allPks);
        Ensure(got.SequenceEqual(allPks), $"the HASH-only query returned {Py.Repr(got)}, expected schema order {Py.Repr(allPks)}");

        // ScanIndexForward=false reverses that same order.
        List<string> want = [.. Enumerable.Reverse(allPks)];
        got = await Poll(ctx, () => Query("gpk = :g", Item((":g", S("g1"))), forward: false), want);
        Ensure(got.SequenceEqual(want), $"ScanIndexForward=false returned {Py.Repr(got)}");

        // A 1-attribute equality prefix (gsk1) narrows to that subset, still ordered by gsk2..gsk4.
        want = ["p1", "p2", "p3", "p4", "p5"];
        got = await Poll(ctx, () => Query("gpk = :g AND gsk1 = :v1", Item((":g", S("g1")), (":v1", S("eng")))), want);
        Ensure(got.SequenceEqual(want), $"the gsk1 equality prefix returned {Py.Repr(got)}");

        // A 2-attribute equality prefix (gsk1, gsk2) narrows further.
        want = ["p1", "p2", "p3"];
        got = await Poll(ctx, () => Query(
            "gpk = :g AND gsk1 = :v1 AND gsk2 = :v2",
            Item((":g", S("g1")), (":v1", S("eng")), (":v2", S("api")))), want);
        Ensure(got.SequenceEqual(want), $"the gsk1+gsk2 equality prefix returned {Py.Repr(got)}");

        // A 3-attribute equality prefix (gsk1, gsk2, gsk3) narrows to the pair sharing every
        // attribute but gsk4.
        want = ["p1", "p2"];
        got = await Poll(ctx, () => Query(
            "gpk = :g AND gsk1 = :v1 AND gsk2 = :v2 AND gsk3 = :v3",
            Item((":g", S("g1")), (":v1", S("eng")), (":v2", S("api")), (":v3", S("ic")))), want);
        Ensure(got.SequenceEqual(want), $"the gsk1+gsk2+gsk3 equality prefix returned {Py.Repr(got)}");

        // A range condition on the attribute right after a 1-attribute prefix (position two, gsk2):
        // a "<" comparator on gsk2 narrows the same way the gsk2 equality prefix above did.
        want = ["p1", "p2", "p3"];
        got = await Poll(ctx, () => Query(
            "gpk = :g AND gsk1 = :v1 AND gsk2 < :v2",
            Item((":g", S("g1")), (":v1", S("eng")), (":v2", S("web")))), want);
        Ensure(got.SequenceEqual(want), $"a '<' condition on gsk2 after the gsk1 prefix returned {Py.Repr(got)}");

        // A range condition on the attribute right after a 3-attribute prefix (position four, gsk4):
        // begins_with narrows the pair down to the one item whose gsk4 matches the prefix.
        want = ["p1"];
        got = await Poll(ctx, () => Query(
            "gpk = :g AND gsk1 = :v1 AND gsk2 = :v2 AND gsk3 = :v3 AND begins_with(gsk4, :v4)",
            Item((":g", S("g1")), (":v1", S("eng")), (":v2", S("api")), (":v3", S("ic")), (":v4", S("2023")))), want);
        Ensure(got.SequenceEqual(want), $"begins_with on gsk4 after the gsk1+gsk2+gsk3 prefix returned {Py.Repr(got)}");

        // The same position-four range, expressed as BETWEEN, selects the same single item.
        want = ["p1"];
        got = await Poll(ctx, () => Query(
            "gpk = :g AND gsk1 = :v1 AND gsk2 = :v2 AND gsk3 = :v3 AND gsk4 BETWEEN :lo AND :hi",
            Item((":g", S("g1")), (":v1", S("eng")), (":v2", S("api")), (":v3", S("ic")), (":lo", S("2020")), (":hi", S("2023")))), want);
        Ensure(got.SequenceEqual(want), $"BETWEEN on gsk4 after the gsk1+gsk2+gsk3 prefix returned {Py.Repr(got)}");

        // Clause order in KeyConditionExpression does not matter: the position-four BETWEEN query
        // above, with every clause reordered, selects the same item.
        want = ["p1"];
        got = await Poll(ctx, () => Query(
            "gsk4 BETWEEN :lo AND :hi AND gsk3 = :v3 AND gpk = :g AND gsk2 = :v2 AND gsk1 = :v1",
            Item((":g", S("g1")), (":v1", S("eng")), (":v2", S("api")), (":v3", S("ic")), (":lo", S("2020")), (":hi", S("2023")))), want);
        Ensure(got.SequenceEqual(want), $"reordering the clauses changed the result to {Py.Repr(got)}");
    }
}

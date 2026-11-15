// Translated from behavioral/dynamodb/check_query_filter_expression_operators_match_scan_twin_in_partition.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-filter-expression-operators-match-scan-twin-in-partition</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryFilterExpressionOperatorsMatchScanTwinInPartition
{
    private const string Pk = "pk";
    private const string Sk = "sk";

    private sealed record Row(string Sk, string Desc, string Category, string[] Roles, int Score, string State);

    /// <summary>Items in the queried partition. Each filter below is designed against these fields.</summary>
    private const string P1 = "p1";

    private static readonly Row[] P1Items =
    [
        new("i1", "fast shipping notice", "alpha-priority", ["admin", "packer"], 5, "placed"),
        new("i2", "international customs hold notice", "beta-priority", ["packer"], 12, "held"),
        new("i3", "notice of refund", "alpha-priority", ["admin"], 20, "placed"),
        new("i4", "late", "gamma-priority", ["reader"], 30, "shipped"),
        new("i5", "notice", "alpha-priority", ["reader", "packer"], 25, "returned"),
    ];

    /// <summary>
    /// One item in a different partition that would match several filters below if it were in
    /// <c>P1</c>: used to prove the Query stays inside the queried partition.
    /// </summary>
    private const string P2 = "p2";

    private static readonly Row P2Item = new("i6", "notice of notice", "alpha-priority", ["admin"], 15, "placed");

    /// <summary>
    /// Every attribute name is aliased, whether or not it is a reserved word; each case lists
    /// only the aliases its own FilterExpression uses (an unused one is rejected).
    /// </summary>
    private static readonly Dictionary<string, string> AllNames = Names(("#d", "desc"), ("#r", "roles"), ("#c", "category"), ("#sc", "score"), ("#st", "state"));

    private static Task Put(IAmazonDynamoDB ddb, string table, string pk, Row item) =>
        ddb.PutItemAsync(table, Item(
            ("pk", S(pk)), ("sk", S(item.Sk)),
            ("desc", S(item.Desc)), ("category", S(item.Category)),
            ("roles", SS([.. item.Roles.Order(StringComparer.Ordinal)])),
            ("score", N(item.Score.ToString(CultureInfo.InvariantCulture))), ("state", S(item.State))));

    private static SortedSet<string> SkSet(IEnumerable<Dictionary<string, AttributeValue>> items) =>
        new(items.Select(i => i["sk"].S), StringComparer.Ordinal);

    /// <summary>Every page of a strongly consistent Scan restricted (client-side) to <paramref name="pk"/>.</summary>
    private static async Task<SortedSet<string>> ScanInPartition(
        IAmazonDynamoDB ddb, string table, string pk, string filterExpression, Dictionary<string, string> names, Dictionary<string, AttributeValue> values)
    {
        var output = new List<Dictionary<string, AttributeValue>>();
        Dictionary<string, AttributeValue>? start = null;
        while (true)
        {
            var page = await ddb.ScanAsync(new ScanRequest
            {
                TableName = table,
                ConsistentRead = true,
                FilterExpression = filterExpression,
                ExpressionAttributeNames = names,
                ExpressionAttributeValues = values,
                ExclusiveStartKey = start,
            });
            output.AddRange((page.Items ?? []).Where(i => i["pk"].S == pk));
            start = page.LastEvaluatedKey;
            if (start is null || start.Count == 0)
            {
                return SkSet(output);
            }
        }
    }

    private static Task<QueryResponse> QueryPartition(
        IAmazonDynamoDB ddb, string table, string pk, string filterExpression, Dictionary<string, string> names, Dictionary<string, AttributeValue> values)
    {
        var allNames = new Dictionary<string, string>(names, StringComparer.Ordinal) { ["#pk"] = "pk" };
        var allValues = new Dictionary<string, AttributeValue>(values, StringComparer.Ordinal) { [":pk"] = S(pk) };
        return ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            ConsistentRead = true,
            KeyConditionExpression = "#pk = :pk",
            ExpressionAttributeNames = allNames,
            ExpressionAttributeValues = allValues,
            FilterExpression = filterExpression,
        });
    }

    /// <summary>
    /// Query FilterExpression operators (contains, NOT contains, begins_with on a non-key
    /// attribute, BETWEEN, IN, a compound OR, size) select inside the queried partition exactly
    /// what the same FilterExpression on Scan selects there, with a fixed ScannedCount.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-filter-expression-operators-match-scan-twin-in-partition")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement(Pk, KeyType.HASH), new KeySchemaElement(Sk, KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition(Pk, ScalarAttributeType.S), new AttributeDefinition(Sk, ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        foreach (var item in P1Items)
        {
            await Put(ddb, table, P1, item);
        }

        await Put(ddb, table, P2, P2Item);

        var partitionSize = P1Items.Length;
        (string Expr, string[] Used, Dictionary<string, AttributeValue> Values, string[] Expected)[] cases =
        [
            ("contains(#d, :v)", ["#d"], Item((":v", S("notice"))), ["i1", "i2", "i3", "i5"]),
            ("contains(#r, :v)", ["#r"], Item((":v", S("admin"))), ["i1", "i3"]),
            ("contains(#r, :v)", ["#r"], Item((":v", S(""))), []),
            ("NOT contains(#r, :v)", ["#r"], Item((":v", S("packer"))), ["i3", "i4"]),
            ("begins_with(#c, :v)", ["#c"], Item((":v", S("alpha"))), ["i1", "i3", "i5"]),
            ("#sc BETWEEN :lo AND :hi", ["#sc"], Item((":lo", N("10")), (":hi", N("25"))), ["i2", "i3", "i5"]),
            ("#st IN (:a, :b)", ["#st"], Item((":a", S("placed")), (":b", S("held"))), ["i1", "i2", "i3"]),
            ("#st = :a OR #c = :b", ["#st", "#c"], Item((":a", S("shipped")), (":b", S("beta-priority"))), ["i2", "i4"]),
            ("size(#d) > :n", ["#d"], Item((":n", N("15"))), ["i1", "i2", "i3"]),
        ];

        foreach (var (expr, used, values, expectedList) in cases)
        {
            var expected = new SortedSet<string>(expectedList, StringComparer.Ordinal);
            var names = used.ToDictionary(k => k, k => AllNames[k], StringComparer.Ordinal);
            var resp = await QueryPartition(ddb, table, P1, expr, new(names, StringComparer.Ordinal), new(values, StringComparer.Ordinal));
            var got = SkSet(resp.Items ?? []);
            Ensure(got.SetEquals(expected), $"Query {Py.Repr(expr)} in partition {Py.Repr(P1)} returned {Py.Repr(got)}, expected {Py.Repr(expected)}");
            Ensure(resp.Count == expected.Count, $"Query {Py.Repr(expr)} Count was {resp.Count}, expected {expected.Count}");
            Ensure(
                resp.ScannedCount == partitionSize,
                $"Query {Py.Repr(expr)} ScannedCount was {resp.ScannedCount}, expected the partition's {partitionSize} items regardless of the filter");

            var twin = await ScanInPartition(ddb, table, P1, expr, new(names, StringComparer.Ordinal), new(values, StringComparer.Ordinal));
            Ensure(
                got.SetEquals(twin),
                $"Query {Py.Repr(expr)} returned {Py.Repr(got)} but the Scan twin restricted to partition {Py.Repr(P1)} returned {Py.Repr(twin)}");
        }
    }
}

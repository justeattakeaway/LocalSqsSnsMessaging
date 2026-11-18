// Translated from behavioral/dynamodb/check_query_filter_legacy_comparison_operators_beyond_eq.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-filter-legacy-comparison-operators-beyond-eq</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryFilterLegacyComparisonOperatorsBeyondEq
{
    private const string Check = "query-filter-legacy-comparison-operators-beyond-eq";

    private sealed record Row(string Sk, string Label, int Code, int Points, string Plan, bool Enabled, string? Memo);

    // label: string substring target; code: numeric NE target; points: numeric GT/BETWEEN target
    // chosen so a lexicographic compare disagrees with a numeric one (9 < 10 < 11 < 100 < 200
    // numerically, but "100" < "11" < "200" < "9" as strings); plan: IN target; enabled: BOOL target;
    // memo: present on some rows only, for NULL/NOT_NULL.
    private static readonly Row[] Rows =
    [
        new("s0", "catalog", 1, 8, "gold", true, "a"),
        new("s1", "banana", 2, 9, "silver", false, "b"),
        new("s2", "category", 1, 10, "bronze", true, null),
        new("s3", "dog", 3, 11, "gold", false, null),
        new("s4", "cathedral", 1, 100, "platinum", true, "c"),
    ];

    private static string[] Sorted(Func<Row, bool> predicate) => [.. Rows.Where(predicate).Select(r => r.Sk).Order(StringComparer.Ordinal)];

    private static readonly string[] Contains = Sorted(r => r.Label.Contains("cat", StringComparison.Ordinal));
    private static readonly string[] Ne = Sorted(r => r.Code != 1);
    private static readonly string[] Gt = Sorted(r => r.Points > 9);
    private static readonly string[] Between = Sorted(r => r.Points is >= 9 and <= 11);
    private static readonly string[] In = Sorted(r => r.Plan is "gold" or "silver");
    private static readonly string[] BoolEq = Sorted(r => r.Enabled);
    private static readonly string[] Null = Sorted(r => r.Memo is null);
    private static readonly string[] NotNull = Sorted(r => r.Memo is not null);

    private static Dictionary<string, AttributeValue> RowItem(Row row)
    {
        var output = Item(
            ("pk", S("p1")), ("sk", S(row.Sk)), ("label", S(row.Label)),
            ("code", N(row.Code.ToString(CultureInfo.InvariantCulture))), ("points", N(row.Points.ToString(CultureInfo.InvariantCulture))),
            ("plan", S(row.Plan)), ("enabled", Bool(row.Enabled)));
        if (row.Memo is not null)
        {
            output["memo"] = S(row.Memo);
        }

        return output;
    }

    private static string[] Ids(QueryResponse resp) => [.. resp.Items.Select(i => i["sk"].S).Order(StringComparer.Ordinal)];

    /// <summary>Python's repr of a tuple of strings.</summary>
    private static string TupleRepr(string[] values) =>
        values.Length == 1 ? $"({Py.Repr(values[0])},)" : "(" + string.Join(", ", values.Select(v => Py.Repr(v))) + ")";

    private static Dictionary<string, Condition> Filter(string attribute, ComparisonOperator op, params AttributeValue[] values) =>
        new(StringComparer.Ordinal)
        {
            [attribute] = values.Length == 0
                ? new Condition { ComparisonOperator = op }
                : new Condition { ComparisonOperator = op, AttributeValueList = [.. values] },
        };

    /// <summary>
    /// Query's legacy QueryFilter evaluates CONTAINS, NE, GT, BETWEEN (N numerically), IN, BOOL
    /// EQ, NULL and NOT_NULL exactly as their FilterExpression twin, over the whole partition, and
    /// without lowering ConsumedCapacity relative to an unfiltered Query.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-filter-legacy-comparison-operators-beyond-eq")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);
        foreach (var row in Rows)
        {
            await ddb.PutItemAsync(t, RowItem(row));
        }

        var @base = await ddb.QueryAsync(new QueryRequest
        {
            TableName = t,
            KeyConditionExpression = "#pk = :pk",
            ExpressionAttributeNames = Names(("#pk", "pk")),
            ExpressionAttributeValues = Item((":pk", S("p1"))),
            ConsistentRead = true,
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        });
        Ensure(
            @base.Count == Rows.Length && @base.ScannedCount == Rows.Length,
            $"unfiltered baseline must see every seeded item, got Count={Py.Repr(@base.Count)} ScannedCount={Py.Repr(@base.ScannedCount)} over {Rows.Length} seeded");
        var baselineCu = @base.ConsumedCapacity.CapacityUnits;

        Task<QueryResponse> Legacy(Dictionary<string, Condition> queryFilter) => ddb.QueryAsync(new QueryRequest
        {
            TableName = t,
            KeyConditions = new() { ["pk"] = new Condition { AttributeValueList = [S("p1")], ComparisonOperator = ComparisonOperator.EQ } },
            QueryFilter = queryFilter,
            ConsistentRead = true,
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        });

        Task<QueryResponse> Expr(string filterExpression, Dictionary<string, string> names, Dictionary<string, AttributeValue> values)
        {
            var allNames = new Dictionary<string, string>(StringComparer.Ordinal) { ["#pk"] = "pk" };
            foreach (var (k, v) in names)
            {
                allNames[k] = v;
            }

            var allValues = Item((":pk", S("p1")));
            foreach (var (k, v) in values)
            {
                allValues[k] = v;
            }

            return ddb.QueryAsync(new QueryRequest
            {
                TableName = t,
                KeyConditionExpression = "#pk = :pk",
                FilterExpression = filterExpression,
                ExpressionAttributeNames = allNames,
                ExpressionAttributeValues = allValues,
                ConsistentRead = true,
                ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
            });
        }

        void AssertTwin(string op, QueryResponse legacyResp, QueryResponse exprResp, string[] expected)
        {
            Ensure(Ids(legacyResp).SequenceEqual(expected), $"{op}: legacy QueryFilter returned {TupleRepr(Ids(legacyResp))}, expected {TupleRepr(expected)}");
            Ensure(Ids(exprResp).SequenceEqual(expected), $"{op}: FilterExpression twin returned {TupleRepr(Ids(exprResp))}, expected {TupleRepr(expected)}");
            Ensure(
                legacyResp.Count == exprResp.Count && exprResp.Count == expected.Length,
                $"{op}: legacy Count={Py.Repr(legacyResp.Count)}, twin Count={Py.Repr(exprResp.Count)}, expected {Py.Repr(expected.Length)}");
            Ensure(
                legacyResp.ScannedCount == Rows.Length,
                $"{op}: QueryFilter must scan the whole partition, got ScannedCount={Py.Repr(legacyResp.ScannedCount)}, expected {Rows.Length}");
            var gotCu = legacyResp.ConsumedCapacity.CapacityUnits;
            Ensure(gotCu == baselineCu, $"{op}: QueryFilter changed ConsumedCapacity to {Py.Repr(gotCu)}, baseline was {Py.Repr(baselineCu)}");
        }

        AssertTwin(
            "CONTAINS",
            await Legacy(Filter("label", ComparisonOperator.CONTAINS, S("cat"))),
            await Expr("contains(#lbl, :v)", Names(("#lbl", "label")), Item((":v", S("cat")))),
            Contains);
        AssertTwin(
            "NE",
            await Legacy(Filter("code", ComparisonOperator.NE, N("1"))),
            await Expr("#cod <> :v", Names(("#cod", "code")), Item((":v", N("1")))),
            Ne);
        AssertTwin(
            "GT",
            await Legacy(Filter("points", ComparisonOperator.GT, N("9"))),
            await Expr("#pts > :v", Names(("#pts", "points")), Item((":v", N("9")))),
            Gt);
        AssertTwin(
            "BETWEEN",
            await Legacy(Filter("points", ComparisonOperator.BETWEEN, N("9"), N("11"))),
            await Expr("#pts BETWEEN :lo AND :hi", Names(("#pts", "points")), Item((":lo", N("9")), (":hi", N("11")))),
            Between);
        AssertTwin(
            "IN",
            await Legacy(Filter("plan", ComparisonOperator.IN, S("gold"), S("silver"))),
            await Expr("#pln IN (:a, :b)", Names(("#pln", "plan")), Item((":a", S("gold")), (":b", S("silver")))),
            In);
        AssertTwin(
            "EQ-BOOL",
            await Legacy(Filter("enabled", ComparisonOperator.EQ, Bool(true))),
            await Expr("#en = :v", Names(("#en", "enabled")), Item((":v", Bool(true)))),
            BoolEq);
        AssertTwin(
            "NULL",
            await Legacy(Filter("memo", ComparisonOperator.NULL)),
            await Expr("attribute_not_exists(#memo)", Names(("#memo", "memo")), []),
            Null);
        AssertTwin(
            "NOT_NULL",
            await Legacy(Filter("memo", ComparisonOperator.NOT_NULL)),
            await Expr("attribute_exists(#memo)", Names(("#memo", "memo")), []),
            NotNull);
    }
}

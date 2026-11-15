// Translated from behavioral/dynamodb/check_query_filter_legacy_ands_by_default_ors_under_conditional_operator.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-filter-legacy-ands-by-default-ors-under-conditional-operator</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryFilterLegacyAndsByDefaultOrsUnderConditionalOperator
{
    private const string Check = "query-filter-legacy-ands-by-default-ors-under-conditional-operator";

    private static readonly (string Sk, string Plan, int Points)[] Rows =
    [
        ("s0", "gold", 10),
        ("s1", "gold", 50),
        ("s2", "silver", 50),
        ("s3", "bronze", 5),
    ];

    private static readonly string[] AndMatch = [.. Rows.Where(r => r.Plan == "gold" && r.Points > 20).Select(r => r.Sk).Order(StringComparer.Ordinal)];
    private static readonly string[] OrMatch = [.. Rows.Where(r => r.Plan == "gold" || r.Points > 20).Select(r => r.Sk).Order(StringComparer.Ordinal)];

    private static Dictionary<string, Condition> Filter() => new(StringComparer.Ordinal)
    {
        ["plan"] = new Condition { ComparisonOperator = ComparisonOperator.EQ, AttributeValueList = [S("gold")] },
        ["points"] = new Condition { ComparisonOperator = ComparisonOperator.GT, AttributeValueList = [N("20")] },
    };

    private static string[] Ids(QueryResponse resp) => [.. resp.Items.Select(i => i["sk"].S).Order(StringComparer.Ordinal)];

    /// <summary>Python's repr of a tuple of strings.</summary>
    private static string TupleRepr(string[] values) =>
        values.Length == 1 ? $"({Py.Repr(values[0])},)" : "(" + string.Join(", ", values.Select(v => Py.Repr(v))) + ")";

    /// <summary>
    /// Query's legacy QueryFilter with two conditions ANDs them when ConditionalOperator is
    /// omitted, and ORs them when ConditionalOperator is OR, over the whole scanned partition.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-filter-legacy-ands-by-default-ors-under-conditional-operator")]
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
            await ddb.PutItemAsync(t, Item(
                ("pk", S("p1")), ("sk", S(row.Sk)),
                ("plan", S(row.Plan)), ("points", N(row.Points.ToString(CultureInfo.InvariantCulture)))));
        }

        QueryRequest Request(ConditionalOperator? conditionalOperator) => new()
        {
            TableName = t,
            KeyConditions = new() { ["pk"] = new Condition { AttributeValueList = [S("p1")], ComparisonOperator = ComparisonOperator.EQ } },
            QueryFilter = Filter(),
            ConditionalOperator = conditionalOperator,
            ConsistentRead = true,
        };

        var @default = await ddb.QueryAsync(Request(null));
        Ensure(
            Ids(@default).SequenceEqual(AndMatch),
            $"omitting ConditionalOperator returned {TupleRepr(Ids(@default))}, expected the AND default {TupleRepr(AndMatch)}");
        Ensure(
            @default.ScannedCount == Rows.Length,
            $"the default-AND query scanned {Py.Repr(@default.ScannedCount)}, expected the whole partition of {Rows.Length}");

        var explicitAnd = await ddb.QueryAsync(Request(ConditionalOperator.AND));
        Ensure(
            Ids(explicitAnd).SequenceEqual(AndMatch),
            $"ConditionalOperator=AND returned {TupleRepr(Ids(explicitAnd))}, expected {TupleRepr(AndMatch)} -- the same as the default");

        var ored = await ddb.QueryAsync(Request(ConditionalOperator.OR));
        Ensure(
            Ids(ored).SequenceEqual(OrMatch),
            $"ConditionalOperator=OR returned {TupleRepr(Ids(ored))}, expected the union {TupleRepr(OrMatch)}");
        Ensure(
            ored.ScannedCount == Rows.Length,
            $"the OR query scanned {Py.Repr(ored.ScannedCount)}, expected the whole partition of {Rows.Length}");
        Ensure(!OrMatch.SequenceEqual(AndMatch), "the OR and AND unions coincide, so this scenario does not distinguish them");
    }
}

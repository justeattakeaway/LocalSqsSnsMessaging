// Translated from behavioral/dynamodb/check_partiql_equal_and_not_equal_over_list_map_set_and_mixed_type_in.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-equal-and-not-equal-over-list-map-set-and-mixed-type-in</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlEqualAndNotEqualOverListMapSetAndMixedTypeIn
{
    private const string CheckId = "partiql-equal-and-not-equal-over-list-map-set-and-mixed-type-in";
    private static readonly string[] InMatches = ["num_match", "str_match", "bool_match"];

    // pk -> val, one item per non-scalar type plus a sibling of the same type.
    private static List<(string Pk, AttributeValue Val)> ComplexItems() =>
    [
        ("list1", L(N("1"), N("2"))),
        ("list2", L(N("3"))),
        ("map1", M(("x", N("1")))),
        ("map2", M(("x", N("2")))),
        ("set1", SS("a", "b")),
        ("set2", SS("c")),
    ];

    // pk -> val, one item per scalar type the mixed-type IN list probes.
    private static List<(string Pk, AttributeValue Val)> ScalarItems() =>
    [
        ("num_match", N("1")),
        ("str_match", S("a")),
        ("bool_match", Bool(true)),
        ("num_miss", N("99")),
    ];

    private static async Task<HashSet<string>> Select(IAmazonDynamoDB ddb, string table, string where)
    {
        var items = (await ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = $"SELECT * FROM \"{table}\" WHERE {where}", ConsistentRead = true })).Items;
        return items.Select(i => i["pk"].S).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// PartiQL = and &lt;&gt; compare List, Map and String Set operands by full value, and IN accepts
    /// a list mixing Number, String and Boolean literals, matching each by its own type.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ql-operators.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-equal-and-not-equal-over-list-map-set-and-mixed-type-in")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-pqleq-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 60);

        foreach (var (pk, val) in ComplexItems().Concat(ScalarItems()))
        {
            await ddb.PutItemAsync(table, Item(("pk", S(pk)), ("val", val)));
        }

        var allPks = ComplexItems().Concat(ScalarItems()).Select(i => i.Pk).ToHashSet(StringComparer.Ordinal);

        foreach (var (pk, literal) in new[] { ("list1", "[1, 2]"), ("map1", "{'x': 1}"), ("set1", "<<'a', 'b'>>") })
        {
            var matched = await Select(ddb, table, $"val = {literal}");
            Ensure(matched.SetEquals([pk]), $"val = {literal} returned {Py.Repr(matched.Order(StringComparer.Ordinal).ToList())}, expected [{Py.Repr(pk)}]");

            var notMatched = await Select(ddb, table, $"val <> {literal}");
            Ensure(
                notMatched.SetEquals(allPks.Where(p => p != pk)),
                $"val <> {literal} returned {Py.Repr(notMatched.Order(StringComparer.Ordinal).ToList())}, expected everything but {Py.Repr(pk)}");
        }

        var inMatched = await Select(ddb, table, "val IN (1, 'a', true)");
        Ensure(
            inMatched.SetEquals(InMatches),
            $"val IN (1, 'a', true) returned {Py.Repr(inMatched.Order(StringComparer.Ordinal).ToList())}, expected the number, string and boolean matches only, not the mismatched number");
    }
}

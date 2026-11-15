// Translated from behavioral/dynamodb/check_partiql_contains_and_begins_with_select_exact_matching_set.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-contains-and-begins-with-select-exact-matching-set</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlContainsAndBeginsWithSelectExactMatchingSet
{
    private const string CheckId = "partiql-contains-and-begins-with-select-exact-matching-set";
    private const string Match = "match";
    private const string NoMatch = "nomatch";
    private const string Missing = "missing";

    /// <summary>pk -> attribute values. "missing" carries none of the tested attributes at all.</summary>
    private static List<(string Pk, (string Name, AttributeValue Value)[] Attrs)> Items() =>
    [
        (Match,
        [
            ("s_attr", S("hello world")),
            ("ss_attr", SS("red", "green", "blue")),
            ("ns_attr", NS("1", "2", "3")),
            ("l_attr", L(S("apple"), S("banana"))),
            ("sbw_attr", S("foobar")),
            ("bbw_attr", B("ABCDEF")),
        ]),
        (NoMatch,
        [
            ("s_attr", S("goodbye")),
            ("ss_attr", SS("yellow", "purple")),
            ("ns_attr", NS("10", "20")),
            ("l_attr", L(S("cherry"), S("date"))),
            ("sbw_attr", S("barfoo")),
            ("bbw_attr", B("XYZ123")),
        ]),
        (Missing, []),
    ];

    /// <summary>An on-demand table keyed by pk (S) HASH only.</summary>
    private static async Task<string> MakeTable(FidelityContext ctx)
    {
        var ddb = ctx.Dynamo;
        var name = FidelityContext.UniqueName("sim2real-behav-ddbcb-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 90);
        return name;
    }

    private static async Task Seed(FidelityContext ctx, string name)
    {
        var ddb = ctx.Dynamo;
        foreach (var (pk, attrs) in Items())
        {
            await ddb.PutItemAsync(name, Item([("pk", S(pk)), .. attrs]));
        }
    }

    private static async Task<HashSet<string>> SelectPks(IAmazonDynamoDB ddb, string name, string where, List<AttributeValue>? parameters = null)
    {
        var items = new List<Dictionary<string, AttributeValue>>();
        string? token = null;
        while (true)
        {
            var page = await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
            {
                Statement = $"SELECT pk FROM \"{name}\" WHERE {where}",
                ConsistentRead = true,
                Parameters = parameters,
                NextToken = token,
            });
            items.AddRange(page.Items);
            token = page.NextToken;
            if (string.IsNullOrEmpty(token))
            {
                return items.Select(i => i["pk"].S).ToHashSet(StringComparer.Ordinal);
            }
        }
    }

    private static List<string> Sorted(IEnumerable<string> values) => [.. values.Order(StringComparer.Ordinal)];

    /// <summary>
    /// PartiQL contains() on S/SS/NS/L and begins_with() on S/B select exactly the matching item,
    ///
    /// never the item holding a non-matching value or the item missing the attribute entirely.
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ql-functions.contains.html
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ql-functions.beginswith.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-contains-and-begins-with-select-exact-matching-set")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var name = await MakeTable(ctx);
        await Seed(ctx, name);
        var want = new HashSet<string>(StringComparer.Ordinal) { Match };

        var sGot = await SelectPks(ddb, name, "contains(s_attr, 'wor')");
        Ensure(sGot.SetEquals(want), $"contains(s_attr, 'wor') returned {Py.Repr(Sorted(sGot))}, expected {Py.Repr(Sorted(want))}");

        var ssGot = await SelectPks(ddb, name, "contains(ss_attr, 'red')");
        Ensure(ssGot.SetEquals(want), $"contains(ss_attr, 'red') returned {Py.Repr(Sorted(ssGot))}, expected {Py.Repr(Sorted(want))}");

        var nsGot = await SelectPks(ddb, name, "contains(ns_attr, 2)");
        Ensure(nsGot.SetEquals(want), $"contains(ns_attr, 2) returned {Py.Repr(Sorted(nsGot))}, expected {Py.Repr(Sorted(want))}");

        var lGot = await SelectPks(ddb, name, "contains(l_attr, 'banana')");
        Ensure(lGot.SetEquals(want), $"contains(l_attr, 'banana') returned {Py.Repr(Sorted(lGot))}, expected {Py.Repr(Sorted(want))}");

        var sbwGot = await SelectPks(ddb, name, "begins_with(sbw_attr, 'foo')");
        Ensure(sbwGot.SetEquals(want), $"begins_with(sbw_attr, 'foo') returned {Py.Repr(Sorted(sbwGot))}, expected {Py.Repr(Sorted(want))}");

        var bbwGot = await SelectPks(ddb, name, "begins_with(bbw_attr, ?)", parameters: [B("ABC")]);
        Ensure(bbwGot.SetEquals(want), $"begins_with(bbw_attr, ?) with B prefix returned {Py.Repr(Sorted(bbwGot))}, expected {Py.Repr(Sorted(want))}");
    }
}

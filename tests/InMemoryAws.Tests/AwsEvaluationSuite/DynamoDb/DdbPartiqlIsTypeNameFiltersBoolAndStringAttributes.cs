// Translated from behavioral/dynamodb/check_ddb_partiql_is_type_name_filters_bool_and_string_attributes.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.ddb-partiql-is-type-name-filters-bool-and-string-attributes</summary>
[Category("AwsEvaluationSuite")]
public sealed class DdbPartiqlIsTypeNameFiltersBoolAndStringAttributes
{
    private const string Check = "ddb-partiql-is-type-name-filters-bool-and-string-attributes";

    // Four items under a `pk` (S) HASH key. `flag` is BOOL for two items, STRING for one, and absent
    // for one; `s` is STRING for three items and NUMBER for one. Every expected set below is derived
    // from this seed, never typed separately.
    private static Dictionary<string, AttributeValue>[] SeedItems() =>
    [
        Item(("pk", S("bool-true")), ("flag", Bool(true)), ("s", S("alpha"))),
        Item(("pk", S("bool-false")), ("flag", Bool(false)), ("s", N("7"))),
        Item(("pk", S("no-flag")), ("s", S("beta"))),
        Item(("pk", S("flag-as-string")), ("flag", S("true")), ("s", S("gamma"))),
    ];

    private static readonly HashSet<string> BoolTyped =
        [.. SeedItems().Where(i => i.TryGetValue("flag", out var f) && f.BOOL is not null).Select(i => i["pk"].S)];

    private static readonly HashSet<string> StringTyped =
        [.. SeedItems().Where(i => i["s"].S is not null).Select(i => i["pk"].S)];

    /// <summary>
    /// PartiQL's `attr IS &lt;type name&gt;` predicate selects exactly the items whose stored attribute
    /// type matches the named type; a missing attribute and a value stored under a different type
    /// both fail it.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.ddb-partiql-is-type-name-filters-bool-and-string-attributes")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var name = await MakeTable(ctx);
        await Seed(ctx, name);

        var boolMatches = await SelectPks(ddb, name, "flag IS BOOL");
        Ensure(
            boolMatches.SetEquals(BoolTyped),
            $"flag IS BOOL returned {Py.Repr(Sorted(boolMatches))}, expected the BOOL-typed items " +
            $"{Py.Repr(Sorted(BoolTyped))} only -- a missing attribute and a STRING-typed 'true' must not match");

        var stringMatches = await SelectPks(ddb, name, "s IS STRING");
        Ensure(
            stringMatches.SetEquals(StringTyped),
            $"s IS STRING returned {Py.Repr(Sorted(stringMatches))}, expected the STRING-typed items " +
            $"{Py.Repr(Sorted(StringTyped))} only -- an N-typed value must not match");
    }

    private static List<string> Sorted(IEnumerable<string> values) => [.. values.Order(StringComparer.Ordinal)];

    /// <summary>An on-demand table keyed by pk (S) HASH only.</summary>
    private static async Task<string> MakeTable(FidelityContext ctx)
    {
        var ddb = ctx.Dynamo;
        var name = FidelityContext.UniqueName("sim2real-behav-ddbistype-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
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
        foreach (var item in SeedItems())
        {
            await ddb.PutItemAsync(name, item);
        }
    }

    private static async Task<HashSet<string>> SelectPks(IAmazonDynamoDB ddb, string name, string where)
    {
        var pks = new List<string>();
        string? token = null;
        while (true)
        {
            var page = await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
            {
                Statement = $"SELECT pk FROM \"{name}\" WHERE {where}",
                ConsistentRead = true,
                NextToken = token,
            });
            pks.AddRange(page.Items.Select(i => i["pk"].S));
            token = page.NextToken;
            if (string.IsNullOrEmpty(token))
            {
                return [.. pks];
            }
        }
    }
}

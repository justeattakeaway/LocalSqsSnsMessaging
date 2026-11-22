// Translated from behavioral/dynamodb/check_query_filter_and_binds_tighter_than_or.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-filter-and-binds-tighter-than-or</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryFilterAndBindsTighterThanOr
{
    private const string Check = "query-filter-and-binds-tighter-than-or";
    private const string Pk = "pk";
    private const string Sk = "sk";
    private const string Part = "p1";

    /// <summary>sk, status, category, score, chosen so `A OR B AND C` and `(A OR B) AND C` disagree.</summary>
    private static readonly (string Sk, string Status, string Category, int Score)[] Items =
    [
        ("i1", "active", "premium", 95),
        ("i2", "inactive", "basic", 67),
        ("i3", "pending", "standard", 78),
        ("i4", "suspended", "basic", 45),
        ("i5", "active", "premium", 92),
    ];

    /// <summary>
    /// A Query FilterExpression binds AND tighter than OR: `A OR B AND C` keeps only the
    /// suspended item while `(A OR B) AND C` keeps none.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-filter-and-binds-tighter-than-or")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await Seed(ctx);
        var names = Names(("#s", "status"));
        var values = Item((":su", S("suspended")), (":p", S("pending")), (":n", N("80")));

        var @implicit = await Query(ddb, table, "#s = :su OR #s = :p AND score > :n", names, values);
        Ensure(
            @implicit.SetEquals(["i4"]),
            $"`A OR B AND C` returned {Py.Repr(Sorted(@implicit))}, expected ['i4'] -- AND binds tighter than OR");

        var grouped = await Query(ddb, table, "(#s = :su OR #s = :p) AND score > :n", names, values);
        Ensure(grouped.Count == 0, $"`(A OR B) AND C` returned {Py.Repr(Sorted(grouped))}, expected no record");

        var both = await Query(
            ddb,
            table,
            "#s = :a AND #c = :pr",
            Names(("#s", "status"), ("#c", "category")),
            Item((":a", S("active")), (":pr", S("premium"))));
        Ensure(both.SetEquals(["i1", "i5"]), $"status AND category returned {Py.Repr(Sorted(both))}, expected ['i1', 'i5']");
    }

    /// <summary>A pk/sk table with one partition seeded with <see cref="Items"/>.</summary>
    private static async Task<string> Seed(FidelityContext ctx)
    {
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
            KeySchema = [new KeySchemaElement(Pk, KeyType.HASH), new KeySchemaElement(Sk, KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition(Pk, ScalarAttributeType.S), new AttributeDefinition(Sk, ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        foreach (var (sk, status, category, score) in Items)
        {
            await ddb.PutItemAsync(table, Item(
                (Pk, S(Part)),
                (Sk, S(sk)),
                ("status", S(status)),
                ("category", S(category)),
                ("score", N(score.ToString(CultureInfo.InvariantCulture)))));
        }

        return table;
    }

    private static async Task<HashSet<string>> Query(
        IAmazonDynamoDB ddb, string table, string expr, Dictionary<string, string> names, Dictionary<string, AttributeValue> values)
    {
        var resp = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            ConsistentRead = true,
            KeyConditionExpression = "#pk = :pk",
            ExpressionAttributeNames = new Dictionary<string, string>(names, StringComparer.Ordinal) { ["#pk"] = Pk },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>(values, StringComparer.Ordinal) { [":pk"] = S(Part) },
            FilterExpression = expr,
        });
        Ensure(
            resp.ScannedCount == Items.Length,
            $"Query {Py.Repr(expr)} ScannedCount was {Py.Repr(resp.ScannedCount)}, expected the partition's " +
            $"{Items.Length} items regardless of the filter");
        return [.. resp.Items.Select(i => i[Sk].S)];
    }

    private static List<string> Sorted(HashSet<string> set) => [.. set.Order(StringComparer.Ordinal)];
}

// Translated from behavioral/dynamodb/check_query_filter_between_chains_with_and.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-filter-between-chains-with-and</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryFilterBetweenChainsWithAnd
{
    private const string Check = "query-filter-between-chains-with-and";
    private const string Pk = "pk";
    private const string Sk = "sk";
    private const string Part = "p1";

    /// <summary>sk, score, role, weight, chosen so BETWEEN's own AND never swallows a following clause.</summary>
    private static readonly (string Sk, int Score, string Role, int Weight)[] Items =
    [
        ("i1", 5, "x", 50),
        ("i2", 12, "y", 40),
        ("i3", 20, "x", 30),
        ("i4", 30, "y", 20),
        ("i5", 25, "x", 10),
    ];

    /// <summary>
    /// A Query FilterExpression's `x BETWEEN :lo AND :hi` closes at `:hi`: a logical AND that
    /// follows chains a separate clause rather than widening the BETWEEN's upper bound.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-filter-between-chains-with-and")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await Seed(ctx);

        var oneClause = await Query(
            ddb,
            table,
            "#sc BETWEEN :lo AND :hi AND #r = :r",
            Names(("#sc", "score"), ("#r", "role")),
            Item((":lo", N("10")), (":hi", N("25")), (":r", S("x"))));
        Ensure(
            oneClause.SetEquals(["i3", "i5"]),
            $"`score BETWEEN 10 AND 25 AND role = x` returned {Py.Repr(Sorted(oneClause))}, expected " +
            "['i3', 'i5'] -- BETWEEN's AND closes at :hi, the following AND is a separate clause");

        var twoBetweens = await Query(
            ddb,
            table,
            "#sc BETWEEN :lo AND :hi AND #w BETWEEN :lo2 AND :hi2",
            Names(("#sc", "score"), ("#w", "weight")),
            Item((":lo", N("10")), (":hi", N("25")), (":lo2", N("15")), (":hi2", N("35"))));
        Ensure(
            twoBetweens.SetEquals(["i3"]),
            $"`score BETWEEN 10 AND 25 AND weight BETWEEN 15 AND 35` returned {Py.Repr(Sorted(twoBetweens))}, " +
            "expected ['i3'] -- each BETWEEN keeps its own two-operand bound");

        var tripleChain = await Query(
            ddb,
            table,
            "#sc BETWEEN :lo AND :hi AND #r = :r AND #w BETWEEN :lo2 AND :hi2",
            Names(("#sc", "score"), ("#r", "role"), ("#w", "weight")),
            Item((":lo", N("10")), (":hi", N("25")), (":r", S("x")), (":lo2", N("15")), (":hi2", N("35"))));
        Ensure(
            tripleChain.SetEquals(["i3"]),
            "`score BETWEEN 10 AND 25 AND role = x AND weight BETWEEN 15 AND 35` returned " +
            $"{Py.Repr(Sorted(tripleChain))}, expected ['i3']");
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
        foreach (var (sk, score, role, weight) in Items)
        {
            await ddb.PutItemAsync(table, Item(
                (Pk, S(Part)),
                (Sk, S(sk)),
                ("score", N(score.ToString(CultureInfo.InvariantCulture))),
                ("role", S(role)),
                ("weight", N(weight.ToString(CultureInfo.InvariantCulture)))));
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

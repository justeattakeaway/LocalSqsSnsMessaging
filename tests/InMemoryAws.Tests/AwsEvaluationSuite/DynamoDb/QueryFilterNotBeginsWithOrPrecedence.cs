// Translated from behavioral/dynamodb/check_query_filter_not_begins_with_or_precedence.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-filter-not-begins-with-or-precedence</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryFilterNotBeginsWithOrPrecedence
{
    private const string Check = "query-filter-not-begins-with-or-precedence";
    private const string Pk = "pk";
    private const string Sk = "sk";
    private const string Part = "p1";

    /// <summary>
    /// sk, desc, status, chosen so `NOT begins_with(...) OR ...` and `NOT (begins_with(...) OR ...)`
    /// disagree.
    /// </summary>
    private static readonly (string Sk, string Desc, string Status)[] Items =
    [
        ("i1", "alpha one", "A"),
        ("i2", "beta two", "B"),
        ("i3", "alpha three", "C"),
        ("i4", "gamma four", "D"),
        ("i5", "alpha five", "E"),
    ];

    /// <summary>
    /// A Query FilterExpression binds NOT tighter than OR: `NOT begins_with(...) OR ...` differs
    /// from `NOT (begins_with(...) OR ...)`, and OR still commutes around the NOT.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-filter-not-begins-with-or-precedence")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await Seed(ctx);
        var names = Names(("#d", "desc"), ("#s", "status"));
        var values = Item((":v", S("alpha")), (":d", S("D")));

        var tight = await Query(ddb, table, "NOT begins_with(#d, :v) OR #s = :d", names, values);
        Ensure(
            tight.SetEquals(["i2", "i4"]),
            $"`NOT begins_with(desc, 'alpha') OR status = D` returned {Py.Repr(Sorted(tight))}, expected " +
            "['i2', 'i4'] -- NOT binds tighter than OR");

        var grouped = await Query(ddb, table, "NOT (begins_with(#d, :v) OR #s = :d)", names, values);
        Ensure(
            grouped.SetEquals(["i2"]),
            $"`NOT (begins_with(desc, 'alpha') OR status = D)` returned {Py.Repr(Sorted(grouped))}, " +
            "expected ['i2']");

        var swapped = await Query(ddb, table, "#s = :d OR NOT begins_with(#d, :v)", names, values);
        Ensure(
            swapped.SetEquals(tight),
            $"`status = D OR NOT begins_with(desc, 'alpha')` returned {Py.Repr(Sorted(swapped))}, " +
            $"expected {Py.Repr(Sorted(tight))}: OR commutes");
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
        foreach (var (sk, desc, status) in Items)
        {
            await ddb.PutItemAsync(table, Item((Pk, S(Part)), (Sk, S(sk)), ("desc", S(desc)), ("status", S(status))));
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

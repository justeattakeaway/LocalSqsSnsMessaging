// Translated from behavioral/dynamodb/check_query_filter_not_over_missing_attribute_equality.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-filter-not-over-missing-attribute-equality</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryFilterNotOverMissingAttributeEquality
{
    private const string Check = "query-filter-not-over-missing-attribute-equality";
    private const string Pk = "pk";
    private const string Sk = "sk";
    private const string Part = "p1";

    /// <summary>
    /// A Query FilterExpression comparison against a missing attribute evaluates false, so NOT
    /// over it is true: `NOT deleted = :true` keeps every item without `deleted`, not just `False`.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-filter-not-over-missing-attribute-equality")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await Seed(ctx);

        var positive = await Query(ddb, table, "#del = :true");
        Ensure(positive.SetEquals(["i1"]), $"`deleted = true` returned {Py.Repr(Sorted(positive))}, expected ['i1']");

        var negated = await Query(ddb, table, "NOT #del = :true");
        Ensure(
            negated.SetEquals(["i2", "i3", "i4"]),
            $"`NOT deleted = true` returned {Py.Repr(Sorted(negated))}, expected ['i2', 'i3', 'i4'] -- i2 has " +
            "deleted=False so the raw comparison is false and NOT flips it true; i3/i4 have no " +
            "`deleted` attribute at all, which also makes the raw comparison false");
    }

    /// <summary>A pk/sk table, one partition: i1 deleted=True, i2 deleted=False, i3/i4 no `deleted` at all.</summary>
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
        await ddb.PutItemAsync(table, Item((Pk, S(Part)), (Sk, S("i1")), ("deleted", Bool(true))));
        await ddb.PutItemAsync(table, Item((Pk, S(Part)), (Sk, S("i2")), ("deleted", Bool(false))));
        await ddb.PutItemAsync(table, Item((Pk, S(Part)), (Sk, S("i3"))));
        await ddb.PutItemAsync(table, Item((Pk, S(Part)), (Sk, S("i4"))));
        return table;
    }

    private static async Task<HashSet<string>> Query(IAmazonDynamoDB ddb, string table, string expr)
    {
        var resp = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            ConsistentRead = true,
            KeyConditionExpression = "#pk = :pk",
            ExpressionAttributeNames = Names(("#pk", Pk), ("#del", "deleted")),
            ExpressionAttributeValues = Item((":pk", S(Part)), (":true", Bool(true))),
            FilterExpression = expr,
        });
        Ensure(
            resp.ScannedCount == 4,
            $"Query {Py.Repr(expr)} ScannedCount was {Py.Repr(resp.ScannedCount)}, expected the partition's 4 " +
            "items regardless of the filter");
        return [.. resp.Items.Select(i => i[Sk].S)];
    }

    private static List<string> Sorted(HashSet<string> set) => [.. set.Order(StringComparer.Ordinal)];
}

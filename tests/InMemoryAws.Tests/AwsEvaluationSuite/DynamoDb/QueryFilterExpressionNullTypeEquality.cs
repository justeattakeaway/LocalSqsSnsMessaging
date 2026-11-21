// Translated from behavioral/dynamodb/check_query_filter_expression_null_type_equality.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-filter-expression-null-type-equality</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryFilterExpressionNullTypeEquality
{
    private const string Check = "query-filter-expression-null-type-equality";

    /// <summary>
    /// Query's FilterExpression 'tag = :n' with a NULL-typed :n matches only items whose
    /// 'tag' attribute is itself NULL-typed, not an item lacking 'tag' or holding another type.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-filter-expression-null-type-equality")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = await MakeTable(ctx);
        await ddb.PutItemAsync(t, Item(("pk", S("p1")), ("sk", S("s0")), ("tag", Null())));
        await ddb.PutItemAsync(t, Item(("pk", S("p1")), ("sk", S("s1"))));
        await ddb.PutItemAsync(t, Item(("pk", S("p1")), ("sk", S("s2")), ("tag", S("x"))));
        await ddb.PutItemAsync(t, Item(("pk", S("p1")), ("sk", S("s3")), ("tag", Null())));

        var @base = await ddb.QueryAsync(new QueryRequest
        {
            TableName = t,
            KeyConditionExpression = "#pk = :pk",
            ExpressionAttributeNames = Names(("#pk", "pk")),
            ExpressionAttributeValues = Item((":pk", S("p1"))),
            ConsistentRead = true,
        });
        Ensure(
            @base.Count == 4 && @base.ScannedCount == 4,
            "expected the unfiltered baseline to see all 4 seeded items, got " +
            $"Count={Py.Repr(@base.Count)} ScannedCount={Py.Repr(@base.ScannedCount)}");

        var nullEq = await ddb.QueryAsync(new QueryRequest
        {
            TableName = t,
            KeyConditionExpression = "#pk = :pk",
            FilterExpression = "#t = :n",
            ExpressionAttributeNames = Names(("#pk", "pk"), ("#t", "tag")),
            ExpressionAttributeValues = Item((":pk", S("p1")), (":n", Null())),
            ConsistentRead = true,
        });
        Ensure(
            nullEq.ScannedCount == 4,
            "expected FilterExpression to examine all 4 items before filtering, got " +
            $"ScannedCount={Py.Repr(nullEq.ScannedCount)}");
        Ensure(nullEq.Count == 2, $"expected Count 2 for tag = NULL, got {Py.Repr(nullEq.Count)}");
        Ensure(
            Ids(nullEq.Items).SequenceEqual(["s0", "s3"]),
            $"tag = NULL matched {Repr(Ids(nullEq.Items))}, expected exactly ('s0', 's3'): the item " +
            "missing 'tag' (s1) and the item whose 'tag' is a String (s2) must not match");

        var stringEq = await ddb.QueryAsync(new QueryRequest
        {
            TableName = t,
            KeyConditionExpression = "#pk = :pk",
            FilterExpression = "#t = :s",
            ExpressionAttributeNames = Names(("#pk", "pk"), ("#t", "tag")),
            ExpressionAttributeValues = Item((":pk", S("p1")), (":s", S("x"))),
            ConsistentRead = true,
        });
        Ensure(
            Ids(stringEq.Items).SequenceEqual(["s2"]),
            $"tag = :s with a String operand matched {Repr(Ids(stringEq.Items))}, expected exactly " +
            "('s2',): the NULL-typed items (s0, s3) must not match a String comparand");
    }

    private static async Task<string> MakeTable(FidelityContext ctx)
    {
        var ddb = ctx.Dynamo;
        var name = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 250);
        return name;
    }

    private static List<string> Ids(List<Dictionary<string, AttributeValue>> items) => [.. items.Select(i => i["sk"].S).Order(StringComparer.Ordinal)];

    /// <summary>The repr of the tuple <c>_ids</c> returns.</summary>
    private static string Repr(List<string> ids) => ids.Count == 1 ? $"({Py.Repr(ids[0])},)" : "(" + string.Join(", ", ids.Select(Py.Repr)) + ")";
}

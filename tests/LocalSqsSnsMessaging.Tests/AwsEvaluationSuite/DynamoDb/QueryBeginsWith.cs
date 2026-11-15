// Translated from behavioral/dynamodb/check_query_begins_with.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-begins-with</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryBeginsWith
{
    private const string CheckId = "query-begins-with";

    /// <summary>Query with begins_with on sort key filters correctly.</summary>
    [Test]
    [DisplayName("dynamodb.query-begins-with")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        await ddb.PutItemAsync(table, Item(("pk", S("p")), ("sk", S("order#001")), ("t", S("a"))));
        await ddb.PutItemAsync(table, Item(("pk", S("p")), ("sk", S("order#002")), ("t", S("b"))));
        await ddb.PutItemAsync(table, Item(("pk", S("p")), ("sk", S("profile#1")), ("t", S("c"))));
        var resp = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :pk AND begins_with(sk, :prefix)",
            ExpressionAttributeValues = Item((":pk", S("p")), (":prefix", S("order#"))),
            ConsistentRead = true,
        });
        Ensure(resp.Count == 2, $"expected 2 items matching begins_with(sk, 'order#'), got Count={resp.Count} Items={Py.Repr(resp.Items)}");
        Ensure(
            resp.Items.All(item => item["sk"].S.StartsWith("order#", StringComparison.Ordinal)),
            $"expected every returned item's sk to start with 'order#', got {Py.Repr(resp.Items.Select(item => item["sk"].S).ToList())}");
    }
}

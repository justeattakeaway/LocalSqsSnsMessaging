// Translated from behavioral/dynamodb/check_query_filter_expression.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-filter-expression</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryFilterExpression
{
    /// <summary>
    /// FilterExpression is applied after the key-condition read: ScannedCount reflects the
    /// pre-filter items examined, Count reflects the post-filter items actually returned.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-filter-expression")]
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
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        for (var i = 0; i < 5; i++)
        {
            await ddb.PutItemAsync(table, Item(
                ("pk", S("f")),
                ("sk", S(i.ToString("D3", CultureInfo.InvariantCulture))),
                ("status", S(i % 2 == 0 ? "active" : "inactive"))));
        }

        var resp = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :pk",
            FilterExpression = "#st = :s",
            ExpressionAttributeNames = Names(("#st", "status")),
            ExpressionAttributeValues = Item((":pk", S("f")), (":s", S("active"))),
            ConsistentRead = true,
        });
        Ensure(resp.Count == 3, $"Count must reflect the 3 post-filter 'active' items, got {resp.Count} Items={Py.Repr(resp.Items)}");
        Ensure(resp.ScannedCount == 5, $"ScannedCount must reflect all 5 pre-filter items examined under the key condition, got {resp.ScannedCount}");
    }
}

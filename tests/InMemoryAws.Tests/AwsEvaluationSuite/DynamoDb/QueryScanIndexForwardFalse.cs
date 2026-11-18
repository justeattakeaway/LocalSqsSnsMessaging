// Translated from behavioral/dynamodb/check_query_scan_index_forward_false.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-scan-index-forward-false</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryScanIndexForwardFalse
{
    /// <summary>Query with ScanIndexForward=False returns items in descending sort key order.</summary>
    [Test]
    [DisplayName("dynamodb.query-scan-index-forward-false")]
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
            await ddb.PutItemAsync(table, Item(("pk", S("u")), ("sk", S(i.ToString("D3", CultureInfo.InvariantCulture)))));
        }

        var resp = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :pk",
            ExpressionAttributeValues = Item((":pk", S("u"))),
            ScanIndexForward = false,
            ConsistentRead = true,
        });
        var sks = resp.Items.Select(item => item["sk"].S).ToList();
        var expected = Enumerable.Range(0, 5).Reverse().Select(i => i.ToString("D3", CultureInfo.InvariantCulture)).ToList();
        Ensure(sks.SequenceEqual(expected), $"ScanIndexForward=False must return all 5 items in descending sk order {Py.Repr(expected)}, got {Py.Repr(sks)}");
    }
}

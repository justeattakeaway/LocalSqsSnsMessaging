// Translated from behavioral/dynamodb/check_query_never_returns_an_empty_page_with_a_last_evaluated_key.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-never-returns-an-empty-page-with-a-last-evaluated-key</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryNeverReturnsAnEmptyPageWithALastEvaluatedKey
{
    /// <summary>
    /// Unlike Scan, Query never returns an empty result set together with a LastEvaluatedKey. The
    /// Scan arm is asserted in the same check, because the contract is the ASYMMETRY between them.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-never-returns-an-empty-page-with-a-last-evaluated-key")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        for (var i = 0; i < 4; i++)
        {
            await ddb.PutItemAsync(table, Item(("pk", S($"p{i}"))));
        }

        var q = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :v",
            ExpressionAttributeValues = Item((":v", S("no-such-key"))),
        });
        Ensure(q.Count == 0, $"expected no matches, got {q.Count}");
        Ensure(q.LastEvaluatedKey is null, $"Query must not pair an empty result set with a LastEvaluatedKey, got {Py.Repr(q.LastEvaluatedKey)}");

        // The contrast arm: a Scan MAY return a truncated page, so a LastEvaluatedKey there is correct
        // and the invariant above is specific to Query rather than a general rule about paging.
        var s = await ddb.ScanAsync(new ScanRequest { TableName = table, Limit = 1 });
        Ensure(s.LastEvaluatedKey is not null, "a Scan truncated by Limit must return a LastEvaluatedKey, so the Query rule is an asymmetry");
    }
}

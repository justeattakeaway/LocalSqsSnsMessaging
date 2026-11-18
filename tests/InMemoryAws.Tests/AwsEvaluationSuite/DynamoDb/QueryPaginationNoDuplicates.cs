// Translated from behavioral/dynamodb/check_query_pagination_no_duplicates.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-pagination-no-duplicates</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryPaginationNoDuplicates
{
    private const string Check = "query-pagination-no-duplicates";

    /// <summary>Paginated query never returns duplicate items.</summary>
    [Test]
    [DisplayName("dynamodb.query-pagination-no-duplicates")]
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
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        for (var i = 0; i < 15; i++)
        {
            await ddb.PutItemAsync(table, Item(("pk", S("nodup")), ("sk", S(i.ToString("D4", CultureInfo.InvariantCulture)))));
        }

        var seenSks = new List<string>();
        var pages = 0;
        var request = new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :pk",
            ExpressionAttributeValues = Item((":pk", S("nodup"))),
            Limit = 4,
            ConsistentRead = true,
        };
        while (true)
        {
            var resp = await ddb.QueryAsync(request);
            pages++;
            foreach (var item in resp.Items)
            {
                var sk = item["sk"].S;
                Ensure(!seenSks.Contains(sk), $"Duplicate item: {sk}");
                seenSks.Add(sk);
            }

            if (resp.LastEvaluatedKey is null)
            {
                break;
            }

            request.ExclusiveStartKey = resp.LastEvaluatedKey;
        }

        // 15 items at Limit=4 is at least 4 pages; fewer means the service ignored Limit and the
        // dedup logic above was never exercised across a real page boundary.
        Ensure(pages >= 4, $"expected at least 4 pages for 15 items at Limit=4, got {pages}");
        Ensure(seenSks.Count == 15, $"expected 15 unique items across all pages, got {seenSks.Count}");
    }
}

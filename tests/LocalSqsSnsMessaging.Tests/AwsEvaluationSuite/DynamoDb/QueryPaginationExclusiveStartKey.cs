// Translated from behavioral/dynamodb/check_query_pagination_exclusive_start_key.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-pagination-exclusive-start-key</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryPaginationExclusiveStartKey
{
    /// <summary>Query pagination with ExclusiveStartKey yields all items across pages.</summary>
    [Test]
    [DisplayName("dynamodb.query-pagination-exclusive-start-key")]
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
            Tags = [new Tag { Key = "sim2real", Value = "true" }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        for (var i = 0; i < 10; i++)
        {
            await ddb.PutItemAsync(table, Item(("pk", S("pag")), ("sk", S(i.ToString("D3", CultureInfo.InvariantCulture)))));
        }

        var allItems = new List<Dictionary<string, AttributeValue>>();
        var pages = 0;
        var request = new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :pk",
            ExpressionAttributeValues = Item((":pk", S("pag"))),
            Limit = 3,
            ConsistentRead = true,
        };
        while (true)
        {
            var resp = await ddb.QueryAsync(request);
            pages++;
            allItems.AddRange(resp.Items);
            if (resp.LastEvaluatedKey is null)
            {
                break;
            }

            request.ExclusiveStartKey = resp.LastEvaluatedKey;
        }

        // 10 items at Limit=3 is ceil(10/3) = 4 pages; fewer means Limit/ExclusiveStartKey was
        // not actually honoured and the whole result came back in one shot.
        Ensure(pages == 4, $"expected 4 pages of Limit=3 over 10 items, got {pages} pages -- Limit was not honoured");
        var sks = allItems.Select(item => item["sk"].S).ToList();
        Ensure(sks.Count == 10, $"expected 10 items reassembled across pages, got {sks.Count}: {Py.Repr(sks)}");
        Ensure(sks.Distinct(StringComparer.Ordinal).Count() == 10, $"pagination returned a duplicate sort key across pages: {Py.Repr(sks)}");
        Ensure(sks.SequenceEqual(sks.Order(StringComparer.Ordinal)), $"expected sort keys in ascending order, got {Py.Repr(sks)}");
    }
}

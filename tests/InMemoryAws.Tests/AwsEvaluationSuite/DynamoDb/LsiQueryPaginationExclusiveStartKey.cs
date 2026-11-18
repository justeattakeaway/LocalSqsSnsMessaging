// Translated from behavioral/dynamodb/check_lsi_query_pagination_exclusive_start_key.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.lsi-query-pagination-exclusive-start-key</summary>
[Category("AwsEvaluationSuite")]
public sealed class LsiQueryPaginationExclusiveStartKey
{
    private static readonly string[] LekAttributes = ["pk", "sk", "lsk"];
    private static readonly string[] AllSks = ["0", "1", "2", "3", "4"];

    /// <summary>
    /// Paging a strongly consistent LSI Query with Limit returns LastEvaluatedKey carrying BOTH the
    /// table key and the index sort key, and resuming from it yields the remaining items exactly once.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.lsi-query-pagination-exclusive-start-key")]
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
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("gpk", ScalarAttributeType.S),
                new AttributeDefinition("lsk", ScalarAttributeType.N),
            ],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = "gsi1",
                    KeySchema = [new KeySchemaElement("gpk", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            LocalSecondaryIndexes =
            [
                new LocalSecondaryIndex
                {
                    IndexName = "lsi1",
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("lsk", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.KEYS_ONLY },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        for (var i = 0; i < 5; i++)
        {
            var text = i.ToString(CultureInfo.InvariantCulture);
            await ddb.PutItemAsync(table, Item(("pk", S("a")), ("sk", S(text)), ("lsk", N(text))));
        }

        Task<QueryResponse> Query(Dictionary<string, AttributeValue>? start) => ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = "lsi1",
            Limit = 2,
            KeyConditionExpression = "pk = :v",
            ExpressionAttributeValues = Item((":v", S("a"))),
            ExclusiveStartKey = start,
            ConsistentRead = true,
        });

        var first = await Query(null);
        var lek = first.LastEvaluatedKey;
        Ensure(lek is { Count: > 0 }, "a truncated LSI page must return LastEvaluatedKey");
        Ensure(
            lek.Keys.ToHashSet(StringComparer.Ordinal).IsSupersetOf(LekAttributes),
            $"an LSI LastEvaluatedKey must carry the table key and the index key, got {Py.Repr(lek.Keys.Order(StringComparer.Ordinal).ToList())}");
        var seen = first.Items.Select(i => i["sk"].S).ToList();
        var page = first;
        while (page.LastEvaluatedKey is { Count: > 0 })
        {
            page = await Query(page.LastEvaluatedKey);
            seen.AddRange(page.Items.Select(i => i["sk"].S));
        }

        var sorted = seen.Order(StringComparer.Ordinal).ToList();
        Ensure(sorted.SequenceEqual(AllSks), $"paging must yield each item once, got {Py.Repr(sorted)}");
    }
}

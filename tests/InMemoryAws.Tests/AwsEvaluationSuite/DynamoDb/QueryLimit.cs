// Translated from behavioral/dynamodb/check_query_limit.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-limit</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryLimit
{
    private static string Sk(int i) => i.ToString("D3", CultureInfo.InvariantCulture);

    /// <summary>
    /// An unfiltered Query with Limit=N returns at most N items, and since more items remain
    /// in the partition, reports a LastEvaluatedKey identifying the last item it evaluated.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-limit")]
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
            Tags = [new Tag { Key = "sim2real", Value = "query-limit" }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        for (var i = 0; i < 10; i++)
        {
            await ddb.PutItemAsync(table, Item(("pk", S("lim")), ("sk", S(Sk(i)))));
        }

        var resp = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :pk",
            ExpressionAttributeValues = Item((":pk", S("lim"))),
            Limit = 3,
            ConsistentRead = true,
        });
        Ensure(resp.Items.Count == 3, $"Limit=3 over a 10-item partition must return exactly 3 items, got {resp.Items.Count}: {Py.Repr(resp.Items)}");
        Ensure(resp.Count == 3, $"Count must match the number of items returned, got {resp.Count}");
        Ensure(resp.ScannedCount == 3, $"with no FilterExpression, ScannedCount must equal Count, got {resp.ScannedCount}");
        Ensure(
            Py.Equal(resp.Items[0]["sk"], S("000")) && Py.Equal(resp.Items[2]["sk"], S("002")),
            $"Limit must take the first 3 items in sort-key order (000..002), got {Py.Repr(resp.Items.Select(i => i["sk"]).ToList())}");
        Ensure(resp.LastEvaluatedKey is not null, "a Limit-truncated Query with more items remaining must return a LastEvaluatedKey");
        Ensure(
            Py.Equal(resp.LastEvaluatedKey, Item(("pk", S("lim")), ("sk", S("002")))),
            $"LastEvaluatedKey must be the last evaluated key (sk 002), got {Py.Repr(resp.LastEvaluatedKey)}");

        // Independent terminal read: page forward and confirm the cursor actually reaches the
        // remaining 7 items, disjoint from this page -- not just that a key-shaped value is present.
        var rest = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :pk",
            ExpressionAttributeValues = Item((":pk", S("lim"))),
            ExclusiveStartKey = resp.LastEvaluatedKey,
            ConsistentRead = true,
        });
        Ensure(rest.LastEvaluatedKey is null, $"the remaining 7 items fit in one page, got LastEvaluatedKey={Py.Repr(rest.LastEvaluatedKey)}");
        Ensure(
            rest.Items.Select(i => i["sk"].S).ToHashSet(StringComparer.Ordinal).SetEquals(Enumerable.Range(3, 7).Select(Sk)),
            $"paging past the Limit=3 cursor must yield exactly sk 003..009, got {Py.Repr(rest.Items.Select(i => i["sk"].S).Order(StringComparer.Ordinal).ToList())}");
    }
}

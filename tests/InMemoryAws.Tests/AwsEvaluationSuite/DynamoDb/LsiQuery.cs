// Translated from behavioral/dynamodb/check_lsi_query.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.lsi-query</summary>
[Category("AwsEvaluationSuite")]
public sealed class LsiQuery
{
    private const string CheckId = "lsi-query";

    /// <summary>Query on an LSI returns items sorted by the LSI sort key.</summary>
    [Test]
    [DisplayName("dynamodb.lsi-query")]
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
                new AttributeDefinition("created_at", ScalarAttributeType.S),
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            LocalSecondaryIndexes =
            [
                new LocalSecondaryIndex
                {
                    IndexName = "created-index",
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("created_at", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        await ddb.PutItemAsync(table, Item(("pk", S("lsi")), ("sk", S("a")), ("created_at", S("2024-03-01"))));
        await ddb.PutItemAsync(table, Item(("pk", S("lsi")), ("sk", S("b")), ("created_at", S("2024-01-01"))));
        await ddb.PutItemAsync(table, Item(("pk", S("lsi")), ("sk", S("c")), ("created_at", S("2024-02-01"))));
        var resp = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = "created-index",
            KeyConditionExpression = "pk = :pk",
            ExpressionAttributeValues = Item((":pk", S("lsi"))),
            ConsistentRead = true,
        });
        var items = resp.Items;
        Ensure(items.Count == 3, $"expected all 3 items back from the LSI query, got {items.Count}: {Py.Repr(items)}");
        var dates = items.Select(item => item["created_at"].S).ToList();
        Ensure(dates.SequenceEqual(dates.Order(StringComparer.Ordinal)), $"LSI query did not return items sorted by the LSI sort key created_at: {Py.Repr(dates)}");
    }
}

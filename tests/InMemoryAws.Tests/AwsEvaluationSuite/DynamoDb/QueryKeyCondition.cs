// Translated from behavioral/dynamodb/check_query_key_condition.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-key-condition</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryKeyCondition
{
    private const string Check = "query-key-condition";

    /// <summary>
    /// Query with a KeyConditionExpression returns exactly the five items of its partition in ascending sort-key order,
    /// although they are written out of order and beside another partition.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-key-condition")]
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
            Tags = [new Tag { Key = "sim2real-check", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        List<string> expectedSks = ["item-000", "item-001", "item-002", "item-003", "item-004"];
        foreach (var sk in new[] { "item-003", "item-000", "item-004", "item-001", "item-002" })
        {
            await ddb.PutItemAsync(table, Item(("pk", S("user-1")), ("sk", S(sk))));
        }

        await ddb.PutItemAsync(table, Item(("pk", S("user-2")), ("sk", S("item-000"))));
        var resp = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :pk",
            ExpressionAttributeValues = Item((":pk", S("user-1"))),
            ConsistentRead = true,
        });
        Ensure(resp.Count == 5, $"expected 5 items for user-1, got {Py.Repr(resp.Count)}");
        var sks = resp.Items.Select(item => item["sk"].S).ToList();
        Ensure(sks.SequenceEqual(expectedSks), $"expected SK-ascending order {Py.Repr(expectedSks)}, got {Py.Repr(sks)}");
    }
}

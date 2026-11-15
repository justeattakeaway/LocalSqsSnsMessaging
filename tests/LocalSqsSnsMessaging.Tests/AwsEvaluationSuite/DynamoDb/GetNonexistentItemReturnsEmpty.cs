// Translated from behavioral/dynamodb/check_get_nonexistent_item_returns_empty.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.get-nonexistent-item-returns-empty</summary>
[Category("AwsEvaluationSuite")]
public sealed class GetNonexistentItemReturnsEmpty
{
    private const string CheckId = "get-nonexistent-item-returns-empty";

    /// <summary>GetItem for a non-existent key returns empty (no Item key), not an error.</summary>
    [Test]
    [DisplayName("dynamodb.get-nonexistent-item-returns-empty")]
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
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        var resp = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("ghost"))), ConsistentRead = true });
        Ensure(resp.Item is null, $"expected no Item for a key never written, got {Py.Repr(resp.Item)}");
    }
}

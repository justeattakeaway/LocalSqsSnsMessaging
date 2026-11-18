// Translated from behavioral/dynamodb/check_delete_item.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.delete-item</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteItem
{
    /// <summary>
    /// DeleteItem removes the item: an independent read confirms it is gone, and a
    /// conditional mutation that requires the item to still exist now fails.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.delete-item")]
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
            Tags = [new Tag { Key = "sim2real", Value = "true" }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        var key = Item(("pk", S("to-delete")));
        await ddb.PutItemAsync(table, Item(("pk", S("to-delete")), ("x", S("y"))));

        var before = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(before is not null, $"expected item present before delete, got {Py.Repr(before)}");
        Ensure(before.TryGetValue("x", out var x) && Py.Equal(x, S("y")), $"expected x=y before delete, got {Py.Repr(before)}");

        await ddb.DeleteItemAsync(new DeleteItemRequest { TableName = table, Key = key });

        var after = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(after is null, $"expected item absent after delete, got {Py.Repr(after)}");

        var error = await FidelityContext.CaptureError(() => ddb.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = table,
            Key = key,
            ConditionExpression = "attribute_exists(pk)",
        }));
        if (error is null)
        {
            throw Fail("expected ConditionalCheckFailedException deleting an already-deleted key");
        }

        if (error.Exception is not ConditionalCheckFailedException)
        {
            throw error.Exception;
        }

        Ensure(error.Message == "The conditional request failed", error.Message ?? "None");
    }
}

// Translated from behavioral/dynamodb/check_update_item_mixing_attribute_updates_and_expression_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-mixing-attribute-updates-and-expression-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemMixingAttributeUpdatesAndExpressionRejected
{
    /// <summary>
    /// AttributeUpdates and UpdateExpression are mutually exclusive, and the refusal names the
    /// non-expression parameter.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-mixing-attribute-updates-and-expression-rejected")]
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
        var err = await FidelityContext.CaptureError(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("a"))),
            UpdateExpression = "SET x = :v",
            ExpressionAttributeValues = Item((":v", S("1"))),
            AttributeUpdates = new Dictionary<string, AttributeValueUpdate>
            {
                ["y"] = new AttributeValueUpdate { Value = S("2"), Action = AttributeAction.PUT },
            },
        })) ?? throw Fail("an UpdateItem sending both AttributeUpdates and UpdateExpression: the call was ACCEPTED; real AWS rejects it");
        Ensure(err.Code == "ValidationException", $"expected ValidationException, got {err.Code}");
        const string expected = "1 validation error detected: Can not use both expression and non-expression parameters in " +
            "the same request: Non-expression parameters: {AttributeUpdates} Expression parameters: " +
            "{UpdateExpression}";
        Ensure(err.Message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(err.Message)}");
        Ensure(
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("a"))), ConsistentRead = true })).Item is null,
            "the refused update must not create the item");
    }
}

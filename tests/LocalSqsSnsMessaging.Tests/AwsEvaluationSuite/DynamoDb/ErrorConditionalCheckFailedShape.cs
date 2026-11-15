// Translated from behavioral/dynamodb/check_error_conditional_check_failed_shape.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.error-conditional-check-failed-shape</summary>
[Category("AwsEvaluationSuite")]
public sealed class ErrorConditionalCheckFailedShape
{
    private const string CheckId = "error-conditional-check-failed-shape";
    private const string ErrConditionalCheckFailed = "ConditionalCheckFailedException";

    /// <summary>
    /// ConditionalCheckFailedException includes the correct error shape, and a rejected
    /// conditional PutItem leaves the pre-existing item untouched until the condition is
    /// actually met again.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.error-conditional-check-failed-shape")]
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

        // Positive control: the same condition succeeds while the item does not yet exist,
        // proving the condition is actually evaluated rather than always rejected.
        await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = Item(("pk", S("shape-test")), ("v", N("1"))),
            ConditionExpression = "attribute_not_exists(pk)",
        });

        // Break exactly one thing: the identical condition is now false because the item exists.
        var error = await FidelityContext.CaptureError(() => ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = Item(("pk", S("shape-test")), ("v", N("2"))),
            ConditionExpression = "attribute_not_exists(pk)",
        }));
        Ensure(error is not null, "expected ConditionalCheckFailedException, got a successful PutItem");
        Ensure(error.Code == ErrConditionalCheckFailed, $"expected {ErrConditionalCheckFailed}, got {error.Code ?? "None"}");
        Ensure(
            error.Message == "The conditional request failed",
            $"expected the exact ConditionalCheckFailedException message AWS returns, got {Py.Repr(error.Message)}");
        var message = error.Message;
        Ensure(!string.IsNullOrEmpty(message), $"expected a non-empty Message on {ErrConditionalCheckFailed}, got {error}");

        // Effect absent: an independent read shows the rejected write never landed.
        var afterReject = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("shape-test"))), ConsistentRead = true });
        Ensure(
            Py.Equal(afterReject.Item?.GetValueOrDefault("v"), N("1")),
            $"expected v=1 to survive the rejected write, got {Py.Repr(afterReject.Item)}");

        // Restore: delete the item, prove it is gone, then the identical condition succeeds again.
        await ddb.DeleteItemAsync(table, Item(("pk", S("shape-test"))));
        var afterDelete = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("shape-test"))), ConsistentRead = true });
        Ensure(afterDelete.Item is null, $"expected shape-test to be gone after delete, got {Py.Repr(afterDelete.Item)}");

        await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = Item(("pk", S("shape-test")), ("v", N("3"))),
            ConditionExpression = "attribute_not_exists(pk)",
        });
        var afterRestore = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("shape-test"))), ConsistentRead = true });
        Ensure(
            Py.Equal(afterRestore.Item?.GetValueOrDefault("v"), N("3")),
            $"expected v=3 after the restored write succeeded, got {Py.Repr(afterRestore.Item)}");
    }
}

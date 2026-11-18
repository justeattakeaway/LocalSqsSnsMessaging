// Translated from behavioral/dynamodb/check_deep_expression_attribute_value_in_condition_and_set_rejected_before_conditional_check.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.deep-expression-attribute-value-in-condition-and-set-rejected-before-conditional-check</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeepExpressionAttributeValueInConditionAndSetRejectedBeforeConditionalCheck
{
    private const string Check = "deep-expression-attribute-value-in-condition-and-set-rejected-before-conditional-check";
    private const string ErrValidation = "ValidationException";
    private const string ErrConditional = "ConditionalCheckFailedException";
    private const string NestingMessage =
        "1 validation error detected: Nesting Levels have exceeded supported limits: Attributes in " +
        "the item have nested levels beyond supported limit";

    /// <summary>A chain of `levels` nested maps around one scalar leaf, `levels` + 1 deep in total.</summary>
    private static AttributeValue Deep(int levels)
    {
        var value = S("leaf");
        for (var i = 0; i < levels; i++)
        {
            value = M(("n", value));
        }

        return value;
    }

    /// <summary>(Error.Code, Error.Message) of the ClientError fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task<object>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be rejected, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }

    /// <summary>
    /// A 33-level ExpressionAttributeValue is rejected ValidationException even where the same
    /// call's ConditionExpression would otherwise fail, for both PutItem and UpdateItem SET.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.deep-expression-attribute-value-in-condition-and-set-rejected-before-conditional-check")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-ddbdeep-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 30);

        var deep = Deep(32); // 33 levels, one past the limit.

        // The item already exists, so attribute_not_exists(pk) alone would fail the condition --
        // ConditionalCheckFailedException is the outcome if the deep value were valid.
        await ddb.PutItemAsync(t, Item(("pk", S("put-target")), ("ctr", N("1"))));
        var (code, message) = await Error(async () => await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = t,
            Item = Item(("pk", S("put-target")), ("ctr", N("2"))),
            ConditionExpression = "attribute_not_exists(pk) OR ctr = :deep",
            ExpressionAttributeValues = Item((":deep", deep)),
        }));
        Ensure(
            code == ErrValidation,
            $"PutItem with a 33-level ExpressionAttributeValue must be {ErrValidation} even though the ConditionExpression would separately fail, got {Py.Repr(code)}: {Py.Repr(message)}");
        Ensure(code != ErrConditional, $"nesting-depth validation must run before the conditional check is evaluated, got {Py.Repr(code)}");
        Ensure(message == NestingMessage, $"expected the exact ValidationException message, got {Py.Repr(message)}");
        var unchanged = await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("put-target"))), ConsistentRead = true });
        Ensure(
            unchanged.Item is not null && unchanged.Item.TryGetValue("ctr", out var putCtr) && Py.Equal(putCtr, N("1")),
            $"the refused PutItem must leave the existing item untouched, got {Py.Repr(unchanged.Item)}");

        // UpdateItem SET writing the same deep value, with a ConditionExpression that would separately
        // fail because the current counter does not equal the sentinel it compares against.
        await ddb.PutItemAsync(t, Item(("pk", S("update-target")), ("ctr", N("1"))));
        (code, message) = await Error(async () => await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = Item(("pk", S("update-target"))),
            UpdateExpression = "SET ctr = :deep",
            ConditionExpression = "ctr = :mismatch",
            ExpressionAttributeValues = Item((":deep", deep), (":mismatch", N("999"))),
        }));
        Ensure(
            code == ErrValidation,
            $"UpdateItem SET of a 33-level ExpressionAttributeValue must be {ErrValidation} even though the ConditionExpression would separately fail, got {Py.Repr(code)}: {Py.Repr(message)}");
        Ensure(code != ErrConditional, $"nesting-depth validation must run before the conditional check is evaluated, got {Py.Repr(code)}");
        Ensure(message == NestingMessage, $"expected the exact ValidationException message, got {Py.Repr(message)}");
        unchanged = await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("update-target"))), ConsistentRead = true });
        Ensure(
            unchanged.Item is not null && unchanged.Item.TryGetValue("ctr", out var updCtr) && Py.Equal(updCtr, N("1")),
            $"the refused UpdateItem must leave the existing item untouched, got {Py.Repr(unchanged.Item)}");
    }
}

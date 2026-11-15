// Translated from behavioral/dynamodb/check_update_item_add_type_mismatch.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-add-type-mismatch</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemAddTypeMismatch
{
    private const string ErrValidation = "ValidationException";

    /// <summary>
    /// ADD of a number onto a String attribute is rejected with ValidationException "An operand in the update
    /// expression has an incorrect data type" and leaves both attributes unchanged, while ADD onto a Number
    /// attribute applies (counter reads 15).
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-add-type-mismatch")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        var key = Item(("pk", S("type-mismatch-1")));
        await ddb.PutItemAsync(t, Item(("pk", S("type-mismatch-1")), ("tag", S("not-a-number")), ("counter", N("10"))));

        // Positive control: ADD on a Number attribute succeeds -- without this, a target that
        // rejects every ADD call would be indistinguishable from one that correctly rejects only
        // the type mismatch.
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = key,
            UpdateExpression = "ADD #c :delta",
            ExpressionAttributeNames = Names(("#c", "counter")),
            ExpressionAttributeValues = Item((":delta", N("5"))),
        });
        var controlled = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = key, ConsistentRead = true })).Item;
        Ensure(Py.Equal(controlled["counter"], N("15")), $"positive control ADD did not apply, got {Py.Repr(controlled.GetValueOrDefault("counter"))}");

        var (code, message) = await ErrorCode(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = key,
            UpdateExpression = "ADD tag :delta",
            ExpressionAttributeValues = Item((":delta", N("5"))),
        }));
        Ensure(code == ErrValidation, $"expected {ErrValidation}, got {code}");
        Ensure(
            message == "An operand in the update expression has an incorrect data type",
            $"expected AWS's exact ValidationException message, got {Py.Repr(message)}");

        var unchanged = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = key, ConsistentRead = true })).Item;
        Ensure(Py.Equal(unchanged["tag"], S("not-a-number")), "rejected ADD must leave the attribute type/value untouched");
        Ensure(Py.Equal(unchanged["counter"], N("15")), "rejected ADD on one attribute must not affect a sibling attribute");
    }

    private static async Task<(string? Code, string? Message)> ErrorCode<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error is null
            ? throw Fail($"expected {ErrValidation}, but the call SUCCEEDED and returned {Py.Repr(result)}")
            : (error.Code, error.Message);
    }
}

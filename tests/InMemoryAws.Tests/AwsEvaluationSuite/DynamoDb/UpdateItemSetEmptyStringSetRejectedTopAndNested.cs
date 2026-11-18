// Translated from behavioral/dynamodb/check_update_item_set_empty_string_set_rejected_top_and_nested.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-set-empty-string-set-rejected-top-and-nested</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemSetEmptyStringSetRejectedTopAndNested
{
    private const string Check = "update-item-set-empty-string-set-rejected-top-and-nested";
    private const string ErrValidation = "ValidationException";

    /// <summary>
    /// UpdateItem SET of a zero-member string set is refused ValidationException with AWS's
    /// exact message, at the top level and at a nested document path, leaving the item unchanged.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-set-empty-string-set-rejected-top-and-nested")]
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
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        await ddb.PutItemAsync(t, Item(("pk", S("x")), ("wrapper", M())));

        const string expectedMessage =
            "1 validation error detected: One or more parameter values were invalid: " +
            "An string set  may not be empty";

        var (code, message) = await ErrorCode(
            () => ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = t,
                Key = Item(("pk", S("x"))),
                UpdateExpression = "SET #tg = :e",
                ExpressionAttributeNames = Names(("#tg", "tags")),
                ExpressionAttributeValues = Item((":e", SS())),
            }),
            "UpdateItem SET of a top-level zero-member string set");
        Ensure(code == ErrValidation, $"a zero-member string set at the top level must be refused {ErrValidation}, got {Py.Repr(code)}");
        Ensure(message == expectedMessage, $"expected the message {Py.Repr(expectedMessage)}, got {Py.Repr(message)}");

        (code, message) = await ErrorCode(
            () => ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = t,
                Key = Item(("pk", S("x"))),
                UpdateExpression = "SET #w.#tg = :e",
                ExpressionAttributeNames = Names(("#tg", "tags"), ("#w", "wrapper")),
                ExpressionAttributeValues = Item((":e", SS())),
            }),
            "UpdateItem SET of a nested zero-member string set");
        Ensure(code == ErrValidation, $"a zero-member string set at a nested path must be refused {ErrValidation}, got {Py.Repr(code)}");
        Ensure(message == expectedMessage, $"expected the message {Py.Repr(expectedMessage)}, got {Py.Repr(message)}");

        var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("x"))), ConsistentRead = true })).Item ?? [];
        Ensure(!got.ContainsKey("tags"), $"the refused top-level write must leave no \"tags\" attribute behind, got {Py.Repr(got)}");
        got.TryGetValue("wrapper", out var wrapper);
        Ensure(Py.Equal(wrapper, M()), $"the refused nested write must leave the wrapper map unchanged, got {Py.Repr(wrapper)}");
    }

    /// <summary>The AWS error code and message <paramref name="fn"/> raises; an unexpected success fails the check.</summary>
    private static async Task<(string? Code, string? Message)> ErrorCode(Func<Task<UpdateItemResponse>> fn, string what)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"{what}: the call was ACCEPTED; real AWS rejects it, got {Py.Repr(result?.Attributes)}");
    }
}

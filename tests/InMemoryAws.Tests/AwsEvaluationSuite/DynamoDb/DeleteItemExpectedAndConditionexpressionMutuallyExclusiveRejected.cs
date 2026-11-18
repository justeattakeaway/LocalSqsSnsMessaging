// Translated from behavioral/dynamodb/check_delete_item_expected_and_conditionexpression_mutually_exclusive_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.delete-item-expected-and-conditionexpression-mutually-exclusive-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteItemExpectedAndConditionexpressionMutuallyExclusiveRejected
{
    private const string Check = "delete-item-expected-and-conditionexpression-mutually-exclusive-rejected";
    private const string ErrValidation = "ValidationException";

    /// <summary>
    /// DeleteItem supplying both the legacy Expected and ConditionExpression is rejected with
    /// ValidationException and its exact message, while ConditionExpression alone deletes the item.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.delete-item-expected-and-conditionexpression-mutually-exclusive-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-delexpr-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        await ddb.PutItemAsync(table, Item(("pk", S("x"))));

        var error = await FidelityContext.CaptureError(() => ddb.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("x"))),
            Expected = new Dictionary<string, ExpectedAttributeValue>(StringComparer.Ordinal) { ["pk"] = new ExpectedAttributeValue { Exists = true } },
            ConditionExpression = "attribute_exists(pk)",
        }));
        var code = error?.Code;
        var message = error?.Message;
        Ensure(code == ErrValidation, $"expected {ErrValidation}, got {code ?? "None"}");
        const string expectedMessage =
            "1 validation error detected: Can not use both expression and non-expression parameters " +
            "in the same request: Non-expression parameters: {Expected} Expression parameters: " +
            "{ConditionExpression}";
        Ensure(message == expectedMessage, $"expected AWS's exact message, got {Py.Repr(message)}");
        Ensure(
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("x"))), ConsistentRead = true })).Item is not null,
            "the rejected DeleteItem must not have removed the item");

        await ddb.DeleteItemAsync(new DeleteItemRequest { TableName = table, Key = Item(("pk", S("x"))), ConditionExpression = "attribute_exists(pk)" });
        Ensure(
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("x"))), ConsistentRead = true })).Item is null,
            "positive control: ConditionExpression alone must delete the item");
    }
}

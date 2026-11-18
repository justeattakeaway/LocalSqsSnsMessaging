// Translated from behavioral/dynamodb/check_error_validation_empty_key.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.error-validation-empty-key</summary>
[Category("AwsEvaluationSuite")]
public sealed class ErrorValidationEmptyKey
{
    private const string ErrValidation = "ValidationException";

    /// <summary>
    /// GetItem with empty string key value raises ValidationException; a non-empty key on the
    /// same table still succeeds, proving the table itself is healthy and this isn't a target that
    /// rejects every GetItem.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.error-validation-empty-key")]
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

        await ddb.PutItemAsync(table, Item(("pk", S("valid-key"))));
        var control = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("valid-key"))), ConsistentRead = true });
        Ensure(control.Item is not null, $"positive control: expected Item for a non-empty key, got {Py.Repr(control.Item)}");

        var error = await FidelityContext.CaptureError(() => ddb.GetItemAsync(new GetItemRequest
        {
            TableName = table,
            Key = Item(("pk", S(""))),
            ConsistentRead = true,
        }));
        var code = error?.Code;
        var message = error?.Message;
        Ensure(code == ErrValidation, $"expected {ErrValidation}, got {code ?? "None"}");
        Ensure(
            message == "One or more parameter values are not valid. The AttributeValue for a key attribute cannot contain an empty string value. Key: pk",
            $"expected the exact ValidationException message AWS returns, got {Py.Repr(message)}");
    }
}

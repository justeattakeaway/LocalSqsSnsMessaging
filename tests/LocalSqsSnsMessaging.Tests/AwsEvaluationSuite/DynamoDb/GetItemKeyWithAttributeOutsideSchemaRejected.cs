// Translated from behavioral/dynamodb/check_get_item_key_with_attribute_outside_schema_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.get-item-key-with-attribute-outside-schema-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class GetItemKeyWithAttributeOutsideSchemaRejected
{
    private const string ErrValidation = "ValidationException";
    private const string ErrMessage = "The provided key element does not match the schema";

    /// <summary>
    /// GetItem whose Key carries an attribute outside the table's key schema is refused with
    /// ValidationException; the same item read by its exact key still succeeds.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_GetItem.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.get-item-key-with-attribute-outside-schema-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-key-schema-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await ddb.WaitForTableExists(table, delaySeconds: 1, maxAttempts: 60);

        await ddb.PutItemAsync(table, Item(("pk", S("p1")), ("v", S("val"))));

        var (code, message) = await Error(() => ddb.GetItemAsync(new GetItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("p1")), ("extra", S("x"))),
            ConsistentRead = true,
        }));
        Ensure(code == ErrValidation, $"expected {ErrValidation}, got {Py.Repr(code)}: {Py.Repr(message)}");
        Ensure(message == ErrMessage, $"expected message {Py.Repr(ErrMessage)}, got {Py.Repr(message)}");

        var ok = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("p1"))), ConsistentRead = true })).Item;
        Ensure(Py.Equal(ok, Item(("pk", S("p1")), ("v", S("val")))), $"a fully-specified key returned {Py.Repr(ok)}");
    }

    /// <summary>A cleanup running <c>undo()</c> where a failed delete raises.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };

    /// <summary>(Error.Code, Error.Message) of the ClientError fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
        }

        return (error.Code, error.Message);
    }
}

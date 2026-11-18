// Translated from behavioral/dynamodb/check_transact_write_exceeds_100_items.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-exceeds-100-items</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteExceeds100Items
{
    private const string CheckId = "transact-write-exceeds-100-items";
    private const string ErrValidation = "ValidationException";

    /// <summary>
    /// TransactWriteItems with exactly 100 Puts succeeds and writes them; 101 Puts is rejected with
    /// ValidationException and writes nothing, since a transaction is all-or-nothing.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-exceeds-100-items")]
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
            Tags = [new Tag { Key = "sim2real-behav", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems = [.. Enumerable.Range(0, 100).Select(i => new TransactWriteItem { Put = new Put { TableName = t, Item = Item(("pk", S($"txn-atlimit-{i}"))) } })],
        });
        Ensure(await Get(ddb, t, "txn-atlimit-0") is not null, "a transaction at the documented 100-item ceiling must be applied in full");
        Ensure(await Get(ddb, t, "txn-atlimit-99") is not null, "a transaction at the documented 100-item ceiling must be applied in full");

        var (code, message) = await ErrorCode(() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems = [.. Enumerable.Range(0, 101).Select(i => new TransactWriteItem { Put = new Put { TableName = t, Item = Item(("pk", S($"txn-over100-{i}"))) } })],
        }));
        Ensure(code == ErrValidation, $"expected {ErrValidation}, got {code}");
        Ensure(
            message == "1 validation error detected: Value at 'TransactItems' failed to satisfy constraint: Member must have length less than or equal to 100",
            $"expected AWS's exact ValidationException message, got {Py.Repr(message)}");

        Ensure(await Get(ddb, t, "txn-over100-0") is null, "a rejected oversized transaction must not have written any item");
    }

    /// <summary>The error code and message fn() raises; RAISES if the call unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> ErrorCode(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn)
            ?? throw Fail("expected the call to be rejected, but it succeeded");
        return (error.Code, error.Message);
    }

    private static async Task<Dictionary<string, AttributeValue>?> Get(IAmazonDynamoDB ddb, string table, string pk) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;
}

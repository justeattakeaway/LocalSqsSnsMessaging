// Translated from behavioral/dynamodb/check_transact_write_items_over_the_item_cap_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-items-over-the-item-cap-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteItemsOverTheItemCapRejected
{
    /// <summary>
    /// TransactWriteItems is capped at 100 actions. The 101st makes the whole request invalid, and
    /// nothing is written.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-items-over-the-item-cap-rejected")]
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
        var err = await ErrorCode(
            () => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                TransactItems =
                [
                    .. Enumerable.Range(0, 101).Select(i => new TransactWriteItem
                    {
                        Put = new Put { TableName = table, Item = Item(("pk", S("k" + i.ToString(CultureInfo.InvariantCulture)))) },
                    }),
                ],
            }),
            "a 101-action TransactWriteItems");
        Ensure(err.Code == "ValidationException", $"expected ValidationException, got {err.Code}");
        var message = err.Message;
        const string expectedMessage = "1 validation error detected: Value at 'TransactItems' failed to satisfy "
            + "constraint: Member must have length less than or equal to 100";
        Ensure(message == expectedMessage, $"expected message {Py.Repr(expectedMessage)}, got {Py.Repr(message)}");
        Ensure(
            (await ddb.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = true })).Count == 0,
            "a rejected transaction must write nothing");
    }

    private static async Task<AwsError> ErrorCode(Func<Task> call, string what) =>
        await FidelityContext.CaptureError(call)
        ?? throw Fail($"{what}: the call was ACCEPTED; real AWS rejects it");
}

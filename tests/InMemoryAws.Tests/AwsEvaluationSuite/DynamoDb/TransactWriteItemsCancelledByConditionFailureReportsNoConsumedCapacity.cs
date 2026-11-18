// Translated from behavioral/dynamodb/check_transact_write_items_cancelled_by_condition_failure_reports_no_consumed_capacity.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-items-cancelled-by-condition-failure-reports-no-consumed-capacity</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteItemsCancelledByConditionFailureReportsNoConsumedCapacity
{
    private const string Check = "transact-write-items-cancelled-by-condition-failure-reports-no-consumed-capacity";

    private static readonly (string Code, string Message) Canceled = (
        "TransactionCanceledException",
        "Transaction cancelled, please refer cancellation reasons for specific reasons [ConditionalCheckFailed]");

    /// <summary>
    /// A TransactWriteItems cancelled by a failing condition reports no ConsumedCapacity in its
    /// error response and leaves the item unchanged; the same call against a free key commits and
    /// reports 2 write capacity units for its small item.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-items-cancelled-by-condition-failure-reports-no-consumed-capacity")]
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
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        var existing = Item(("pk", S("present")), ("payload", S("original")));
        await ddb.PutItemAsync(table, Item(("pk", S("present")), ("payload", S("original"))));

        // The check reads the whole error response (exc.response), so this call goes out raw: the
        // .NET SDK's exception doesn't expose members of the error body beyond its own model.
        var response = await ExpectError(
            ctx,
            "TransactWriteItems",
            JsonSerializer.Serialize(new
            {
                ReturnConsumedCapacity = "INDEXES",
                TransactItems = new[]
                {
                    new
                    {
                        Put = new
                        {
                            TableName = table,
                            Item = new { pk = new { S = "present" }, payload = new { S = "clobbered" } },
                            ConditionExpression = "attribute_not_exists(pk)",
                        },
                    },
                },
            }),
            Canceled.Code,
            Canceled.Message);
        if (response.TryGetProperty("ConsumedCapacity", out var consumed))
        {
            throw Fail($"a cancelled TransactWriteItems error response carried ConsumedCapacity {Py.Repr(consumed)}, expected none");
        }
        var unchanged = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("present"))), ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(unchanged, existing),
            $"the item is {Py.Repr(unchanged)} after a cancelled transaction, expected it unchanged at {Py.Repr(existing)}");

        // --- positive control: the same shape of call against a free key commits and reports its units ---
        var committed = (await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES,
            TransactItems =
            [
                new TransactWriteItem
                {
                    Put = new Put
                    {
                        TableName = table,
                        Item = Item(("pk", S("free")), ("payload", S("new"))),
                        ConditionExpression = "attribute_not_exists(pk)",
                    },
                },
            ],
        })).ConsumedCapacity[0];
        Ensure(
            committed.WriteCapacityUnits == 2.0,
            $"a committed TransactWriteItems of a small item reported WriteCapacityUnits {Py.Repr(committed.WriteCapacityUnits)}, expected 2.0");
        Ensure(
            committed.CapacityUnits == 2.0,
            $"a committed TransactWriteItems of a small item reported CapacityUnits {Py.Repr(committed.CapacityUnits)}, expected 2.0");
        Ensure(
            Py.Equal(
                (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("free"))), ConsistentRead = true })).Item,
                Item(("pk", S("free")), ("payload", S("new")))),
            "the positive control item was not committed as written");
    }

    /// <summary>
    /// Sends the request, asserts it is refused with (code, message) and returns the error
    /// response body for inspection; raises on an unexpected success.
    /// </summary>
    private static async Task<JsonElement> ExpectError(FidelityContext ctx, string operation, string body, string code, string message)
    {
        var raw = await ctx.DynamoDbRaw(operation, body);
        if ((int)raw.StatusCode < 400)
        {
            throw Fail($"expected {code} ({Py.Repr(message)}), but the call succeeded: {raw.Body}");
        }

        Ensure(raw.ErrorCode == code, $"expected error code {Py.Repr(code)}, got {Py.Repr(raw.ErrorCode)}");
        Ensure(raw.ErrorMessage == message, $"expected message {Py.Repr(message)}, got {Py.Repr(raw.ErrorMessage)}");
        return raw.Json!.Value;
    }
}

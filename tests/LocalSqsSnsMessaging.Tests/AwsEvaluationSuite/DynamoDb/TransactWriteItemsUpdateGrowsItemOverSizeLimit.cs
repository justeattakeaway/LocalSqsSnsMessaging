// Translated from behavioral/dynamodb/check_transact_write_items_update_grows_item_over_size_limit.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-items-update-grows-item-over-size-limit</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteItemsUpdateGrowsItemOverSizeLimit
{
    private const string Check = "transact-write-items-update-grows-item-over-size-limit";
    private const string ErrValidation = "ValidationException";
    private const int LimitBytes = 409600;

    /// <summary>The Error Code and Message <paramref name="fn"/> raises; an unexpected success raises too.</summary>
    private static async Task<(string? Code, string? Message)> ErrorCodeAndMessage<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, err) = await FidelityContext.Capture(fn);
        if (err is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (err.Code, err.Message);
    }

    /// <summary>
    /// A TransactWriteItems Update that would grow a stored item past the size limit is
    /// refused as a top-level ValidationException, never a TransactionCanceledException,
    /// and the stored item is left unchanged.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_TransactWriteItems.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-items-update-grows-item-over-size-limit")]
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
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 30);

        // Positive control: TransactWriteItems Update of a small, in-bounds grow succeeds and
        // reads back, so a target that rejects every transactional update fails here too.
        await ddb.PutItemAsync(table, Item(("pk", S("small")), ("data", S("start"))));
        await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem
                {
                    Update = new Update
                    {
                        TableName = table,
                        Key = Item(("pk", S("small"))),
                        UpdateExpression = "SET #d = :v",
                        ExpressionAttributeNames = Names(("#d", "data")),
                        ExpressionAttributeValues = Item((":v", S("grown-but-still-small"))),
                    },
                },
            ],
        });
        var controlGot = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("small"))), ConsistentRead = true });
        Ensure(
            controlGot.Item?.GetValueOrDefault("data")?.S == "grown-but-still-small",
            $"positive control TransactWriteItems Update failed, got {Py.Repr(controlGot.Item)}");

        // The focus: a stored item grown, through TransactWriteItems Update, past the limit.
        var original = new string('o', 1024);
        await ddb.PutItemAsync(table, Item(("pk", S("grow")), ("data", S(original))));

        var grownValue = new string('g', LimitBytes + 1 - Encoding.UTF8.GetByteCount("pk") - Encoding.UTF8.GetByteCount("grow") - Encoding.UTF8.GetByteCount("data"));
        var (code, message) = await ErrorCodeAndMessage(() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem
                {
                    Update = new Update
                    {
                        TableName = table,
                        Key = Item(("pk", S("grow"))),
                        UpdateExpression = "SET #d = :v",
                        ExpressionAttributeNames = Names(("#d", "data")),
                        ExpressionAttributeValues = Item((":v", S(grownValue))),
                    },
                },
            ],
        }));
        Ensure(
            code == ErrValidation,
            $"a TransactWriteItems Update growing an item past {LimitBytes} bytes must be {ErrValidation}, not TransactionCanceledException, got {Py.Repr(code)}: {Py.Repr(message)}");
        Ensure(
            message == "Item size to update has exceeded the maximum allowed size",
            $"expected AWS's exact ValidationException message, got {Py.Repr(message)}");

        var unchanged = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("grow"))), ConsistentRead = true });
        Ensure(
            unchanged.Item?.GetValueOrDefault("data")?.S == original,
            $"a TransactWriteItems Update rejected for size must leave the item unchanged, got {Py.Repr(unchanged.Item)}");
    }
}

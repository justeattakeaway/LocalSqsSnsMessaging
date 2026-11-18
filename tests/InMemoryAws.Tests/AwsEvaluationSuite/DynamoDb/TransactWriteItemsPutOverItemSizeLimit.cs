// Translated from behavioral/dynamodb/check_transact_write_items_put_over_item_size_limit.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-items-put-over-item-size-limit</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteItemsPutOverItemSizeLimit
{
    private const string Check = "transact-write-items-put-over-item-size-limit";
    private const string ErrValidation = "ValidationException";
    private const int LimitBytes = 409600;

    /// <summary>A one-pk, one-filler-attribute item whose aggregate encoded size is exactly total_bytes.</summary>
    private static Dictionary<string, AttributeValue> ItemOfSize(string pkValue, string fillerAttr, int totalBytes)
    {
        var overhead = Encoding.UTF8.GetByteCount("pk") + Encoding.UTF8.GetByteCount(pkValue) + Encoding.UTF8.GetByteCount(fillerAttr);
        var fillerLen = totalBytes - overhead;
        Ensure(fillerLen > 0, $"{totalBytes} bytes is too small for this item shape");
        return Item(("pk", S(pkValue)), (fillerAttr, S(new string('x', fillerLen))));
    }

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
    /// A TransactWriteItems Put of an item one byte over the size limit is refused as a
    /// top-level ValidationException, never a TransactionCanceledException, and writes nothing.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_TransactWriteItems.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-items-put-over-item-size-limit")]
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

        // Positive control: TransactWriteItems Put of a normal item succeeds and reads back,
        // so a target that rejects every transaction (not just oversized ones) fails here.
        var controlItem = Item(("pk", S("control")), ("data", S("ok")));
        await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems = [new TransactWriteItem { Put = new Put { TableName = table, Item = controlItem } }],
        });
        var controlGot = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("control"))), ConsistentRead = true })).Item;
        Ensure(
            controlGot?.GetValueOrDefault("data")?.S == "ok",
            $"positive control TransactWriteItems Put/GetItem round-trip failed, got {Py.Repr(controlGot)}");

        // The focus: an item one byte over the limit, put through TransactWriteItems.
        var overItem = ItemOfSize("over-limit", "data", LimitBytes + 1);
        var (code, message) = await ErrorCodeAndMessage(() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems = [new TransactWriteItem { Put = new Put { TableName = table, Item = overItem } }],
        }));
        Ensure(
            code == ErrValidation,
            $"a {LimitBytes + 1}-byte TransactWriteItems Put must be {ErrValidation}, not TransactionCanceledException, got {Py.Repr(code)}: {Py.Repr(message)}");
        Ensure(
            message == "Item size has exceeded the maximum allowed size",
            $"expected AWS's exact ValidationException message, got {Py.Repr(message)}");

        var rejected = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("over-limit"))), ConsistentRead = true })).Item;
        Ensure(rejected is null, $"a TransactWriteItems Put rejected for size must not persist, got {Py.Repr(rejected)}");
    }
}

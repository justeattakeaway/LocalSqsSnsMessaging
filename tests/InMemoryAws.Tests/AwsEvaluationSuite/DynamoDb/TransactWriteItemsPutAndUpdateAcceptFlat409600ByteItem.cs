// Translated from behavioral/dynamodb/check_transact_write_items_put_and_update_accept_flat_409600_byte_item.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-items-put-and-update-accept-flat-409600-byte-item</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteItemsPutAndUpdateAcceptFlat409600ByteItem
{
    private const string Check = "transact-write-items-put-and-update-accept-flat-409600-byte-item";
    private const string ErrTxnCancelled = "TransactionCanceledException";
    private const int LimitBytes = 409600;

    private static int Utf8(string text) => Encoding.UTF8.GetByteCount(text);

    /// <summary>A one-pk, one-filler-attribute item whose aggregate encoded size is exactly total_bytes.</summary>
    private static Dictionary<string, AttributeValue> ItemOfSize(string pkValue, string fillerAttr, int totalBytes)
    {
        var overhead = Utf8("pk") + Utf8(pkValue) + Utf8(fillerAttr);
        var fillerLen = totalBytes - overhead;
        Ensure(fillerLen > 0, $"{totalBytes} bytes is too small for this item shape");
        return Item(("pk", S(pkValue)), (fillerAttr, S(new string('x', fillerLen))));
    }

    /// <summary>The item's encoded size: every attribute name's bytes plus its string value's bytes.</summary>
    private static int StoredItemSize(Dictionary<string, AttributeValue> item)
    {
        var total = 0;
        foreach (var (name, value) in item)
        {
            var boto = Py.Boto(value);
            var typeCode = boto.Keys.Single();
            Ensure(typeCode == "S", $"attribute {Py.Repr(name)} is type {Py.Repr(typeCode)}, expected S");
            total += Utf8(name) + Utf8(value.S);
        }

        return total;
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
    /// TransactWriteItems Put and Update accept an item whose flat encoded size, key included, is
    /// exactly 409600 bytes; an Update growing an item one byte past it cancels with a ValidationError
    /// reason and leaves the item unchanged.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ServiceQuotas.html#limits-items
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-items-put-and-update-accept-flat-409600-byte-item")]
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

        // TransactWriteItems Put of an item at exactly the flat 409600-byte limit is accepted
        // and reads back whole, so the key's own bytes are not excluded from the count.
        var putPk = FidelityContext.UniqueName("sim2real-behav-put-");
        var putItem = ItemOfSize(putPk, "data", LimitBytes);
        await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems = [new TransactWriteItem { Put = new Put { TableName = table, Item = putItem } }],
        });
        var putGot = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(putPk))), ConsistentRead = true })).Item;
        Ensure(putGot is not null, $"a {LimitBytes}-byte TransactWriteItems Put must persist, got none");
        Ensure(
            putGot["data"].S == putItem["data"].S,
            $"a {LimitBytes}-byte item must round-trip whole through TransactWriteItems Put, got {putGot["data"].S.Length} bytes back");
        Ensure(StoredItemSize(putGot) == LimitBytes, $"stored item size is {StoredItemSize(putGot)}, expected {LimitBytes}");

        // TransactWriteItems Update that grows a stored item to exactly the same flat
        // 409600-byte limit is accepted too.
        var updatePk = FidelityContext.UniqueName("sim2real-behav-upd-");
        await ddb.PutItemAsync(table, Item(("pk", S(updatePk)), ("data", S("start"))));
        var overhead = Utf8("pk") + Utf8(updatePk) + Utf8("data");
        var grownValue = new string('g', LimitBytes - overhead);
        await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem
                {
                    Update = new Update
                    {
                        TableName = table,
                        Key = Item(("pk", S(updatePk))),
                        UpdateExpression = "SET #d = :v",
                        ExpressionAttributeNames = Names(("#d", "data")),
                        ExpressionAttributeValues = Item((":v", S(grownValue))),
                    },
                },
            ],
        });
        var updateGot = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(updatePk))), ConsistentRead = true })).Item;
        Ensure(
            updateGot["data"].S == grownValue,
            $"the grown value must round-trip whole through TransactWriteItems Update, got {updateGot["data"].S.Length} bytes back");
        Ensure(StoredItemSize(updateGot) == LimitBytes, $"stored item size is {StoredItemSize(updateGot)}, expected {LimitBytes}");

        // The focus: a TransactWriteItems Update that would grow a stored item one byte past
        // the same flat limit is refused, and the stored item is left unchanged.
        var overPk = FidelityContext.UniqueName("sim2real-behav-over-");
        await ddb.PutItemAsync(table, Item(("pk", S(overPk)), ("data", S("start"))));
        var overheadOver = Utf8("pk") + Utf8(overPk) + Utf8("data");
        var overValue = new string('g', LimitBytes + 1 - overheadOver);
        var (code, message) = await ErrorCodeAndMessage(() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem
                {
                    Update = new Update
                    {
                        TableName = table,
                        Key = Item(("pk", S(overPk))),
                        UpdateExpression = "SET #d = :v",
                        ExpressionAttributeNames = Names(("#d", "data")),
                        ExpressionAttributeValues = Item((":v", S(overValue))),
                    },
                },
            ],
        }));
        Ensure(
            code == ErrTxnCancelled,
            $"a TransactWriteItems Update growing an item one byte past {LimitBytes} bytes must be {ErrTxnCancelled}, got {Py.Repr(code)}: {Py.Repr(message)}");
        Ensure(
            message == "Transaction cancelled, please refer cancellation reasons for specific reasons [ValidationError]",
            $"expected AWS's exact TransactionCanceledException message, got {Py.Repr(message)}");

        var unchanged = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(overPk))), ConsistentRead = true })).Item;
        Ensure(
            unchanged["data"].S == "start",
            $"a TransactWriteItems Update rejected for size must leave the item unchanged, got {Py.Repr(unchanged)}");
    }
}

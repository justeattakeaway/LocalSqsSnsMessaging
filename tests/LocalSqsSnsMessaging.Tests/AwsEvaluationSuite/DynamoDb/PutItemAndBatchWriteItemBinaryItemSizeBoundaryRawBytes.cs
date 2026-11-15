// Translated from behavioral/dynamodb/check_put_item_and_batch_write_item_binary_item_size_boundary_raw_bytes.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.put-item-and-batch-write-item-binary-item-size-boundary-raw-bytes</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutItemAndBatchWriteItemBinaryItemSizeBoundaryRawBytes
{
    private const string CheckId = "put-item-and-batch-write-item-binary-item-size-boundary-raw-bytes";
    private const string ErrValidation = "ValidationException";
    private const int LimitBytes = 409600;

    /// <summary>
    /// A one-pk, one-filler-attribute item whose aggregate size (name plus raw Binary bytes,
    /// never base64 text) is total_bytes.
    /// </summary>
    private static Dictionary<string, AttributeValue> ItemOfSize(string pkValue, string fillerAttr, int totalBytes)
    {
        var overhead = Encoding.UTF8.GetByteCount("pk") + Encoding.UTF8.GetByteCount(pkValue) + Encoding.UTF8.GetByteCount(fillerAttr);
        var fillerLen = totalBytes - overhead;
        Ensure(fillerLen > 0, $"{totalBytes} bytes is too small for this item shape");
        return Item(("pk", S(pkValue)), (fillerAttr, B(Enumerable.Repeat((byte)'x', fillerLen).ToArray())));
    }

    /// <summary>The Error Code and Message <paramref name="fn"/> raises; an unexpected success raises too.</summary>
    private static async Task<(string? Code, string? Message)> ErrorCodeAndMessage<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
    }

    private static byte[] StoredBytes(GetItemResponse got) =>
        got.Item?.GetValueOrDefault("data")?.B is { } b ? Bytes(b) : [];

    /// <summary>
    /// The 400 KB item-size gate sizes a Binary attribute by its decoded raw byte length, not
    /// its base64 text length: accepted at exactly 409600 raw bytes, refused one raw byte over,
    /// for both PutItem and BatchWriteItem.
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ServiceQuotas.html#limits-items
    /// </summary>
    [Test]
    [DisplayName("dynamodb.put-item-and-batch-write-item-binary-item-size-boundary-raw-bytes")]
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
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 30);

        // PutItem: a Binary attribute at exactly 409600 raw bytes is accepted and round-trips,
        // even though its base64 text is already longer than 409600 characters.
        var atLimit = ItemOfSize("put-at-limit", "data", LimitBytes);
        var atLimitBytes = Bytes(atLimit["data"].B);
        var base64LenAtLimit = Convert.ToBase64String(atLimitBytes).Length;
        Ensure(
            base64LenAtLimit > LimitBytes,
            $"the fixture must exercise raw-vs-base64 divergence; base64 text was only {base64LenAtLimit} chars for a {LimitBytes}-raw-byte attribute");
        await ddb.PutItemAsync(table, atLimit);
        var got = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("put-at-limit"))), ConsistentRead = true });
        var stored = StoredBytes(got);
        Ensure(
            stored.SequenceEqual(atLimitBytes),
            $"a 409600-raw-byte Binary item must round-trip whole through PutItem, got {stored.Length} raw bytes back");

        // PutItem: one raw byte over is rejected and nothing is stored.
        var over = ItemOfSize("put-over-limit", "data", LimitBytes + 1);
        var (code, message) = await ErrorCodeAndMessage(() => ddb.PutItemAsync(table, over));
        Ensure(
            code == ErrValidation,
            $"a {LimitBytes + 1}-raw-byte Binary PutItem must be {ErrValidation}, got {Py.Repr(code)}: {Py.Repr(message)}");
        Ensure(
            message == "Item size has exceeded the maximum allowed size",
            $"expected AWS's exact ValidationException message, got {Py.Repr(message)}");
        var rejected = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("put-over-limit"))), ConsistentRead = true });
        Ensure(rejected.Item is null, $"a PutItem rejected for size must not persist, got {Py.Repr(rejected.Item)}");

        // BatchWriteItem PutRequest: exactly at the raw-byte limit is accepted and round-trips.
        var bwAtLimit = ItemOfSize("batch-at-limit", "data", LimitBytes);
        var bwResp = await ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new Dictionary<string, List<WriteRequest>>(StringComparer.Ordinal)
            {
                [table] = [new WriteRequest { PutRequest = new PutRequest { Item = bwAtLimit } }],
            },
        });
        Ensure(
            bwResp.UnprocessedItems is null || bwResp.UnprocessedItems.Count == 0,
            $"a 409600-raw-byte Binary BatchWriteItem PutRequest must fully process, got UnprocessedItems={Py.Repr(bwResp.UnprocessedItems)}");
        var bwGot = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("batch-at-limit"))), ConsistentRead = true });
        var bwStored = StoredBytes(bwGot);
        Ensure(
            bwStored.SequenceEqual(Bytes(bwAtLimit["data"].B)),
            $"a 409600-raw-byte Binary item must round-trip whole through BatchWriteItem, got {bwStored.Length} raw bytes back");

        // BatchWriteItem PutRequest: one raw byte over is rejected and nothing is stored.
        var bwOver = ItemOfSize("batch-over-limit", "data", LimitBytes + 1);
        var (bwCode, bwMessage) = await ErrorCodeAndMessage(() => ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new Dictionary<string, List<WriteRequest>>(StringComparer.Ordinal)
            {
                [table] = [new WriteRequest { PutRequest = new PutRequest { Item = bwOver } }],
            },
        }));
        Ensure(
            bwCode == ErrValidation,
            $"a {LimitBytes + 1}-raw-byte Binary BatchWriteItem PutRequest must be {ErrValidation}, got {Py.Repr(bwCode)}: {Py.Repr(bwMessage)}");
        Ensure(
            bwMessage == "Item size has exceeded the maximum allowed size",
            $"expected AWS's exact ValidationException message, got {Py.Repr(bwMessage)}");
        var bwRejected = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("batch-over-limit"))), ConsistentRead = true });
        Ensure(bwRejected.Item is null, $"a BatchWriteItem PutRequest rejected for size must not persist, got {Py.Repr(bwRejected.Item)}");
    }
}

// Translated from behavioral/dynamodb/check_put_item_and_batch_write_item_multibyte_string_size_utf8_bytes.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.put-item-and-batch-write-item-multibyte-string-size-utf8-bytes</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutItemAndBatchWriteItemMultibyteStringSizeUtf8Bytes
{
    private const string CheckId = "put-item-and-batch-write-item-multibyte-string-size-utf8-bytes";
    private const string ErrValidation = "ValidationException";
    private const int LimitBytes = 409600;

    /// <summary>
    /// A string whose UTF-8 encoding is exactly <paramref name="byteLen"/> bytes, built mostly from a 2-byte
    /// character so its character count differs from its byte count.
    /// </summary>
    private static string MultibyteFiller(int byteLen)
    {
        var pairs = byteLen / 2;
        var remainder = byteLen % 2;
        var filler = new string('é', pairs);
        if (remainder != 0)
        {
            filler += "x";
        }

        Ensure(Encoding.UTF8.GetByteCount(filler) == byteLen, "filler construction must be byte-exact");
        return filler;
    }

    /// <summary>A one-pk, one-filler-attribute item whose aggregate UTF-8 encoded size is totalBytes.</summary>
    private static Dictionary<string, AttributeValue> ItemOfSize(string pkValue, string fillerAttr, int totalBytes)
    {
        var overhead = Encoding.UTF8.GetByteCount("pk") + Encoding.UTF8.GetByteCount(pkValue) + Encoding.UTF8.GetByteCount(fillerAttr);
        var fillerLen = totalBytes - overhead;
        Ensure(fillerLen > 0, $"{totalBytes} bytes is too small for this item shape");
        return Item(("pk", S(pkValue)), (fillerAttr, S(MultibyteFiller(fillerLen))));
    }

    /// <summary>The Error Code and Message <paramref name="fn"/> raises; an unexpected success raises too.</summary>
    private static async Task<(string? Code, string? Message)> ErrorCodeAndMessage(Func<Task<object>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }

    /// <summary>
    /// A multi-byte-character string item sized by its UTF-8 byte length is accepted at exactly
    /// the 409600-byte limit and refused one byte over, for both PutItem and BatchWriteItem.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.put-item-and-batch-write-item-multibyte-string-size-utf8-bytes")]
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

        // PutItem: a multi-byte-character string at exactly the byte limit is accepted and round-trips.
        var atLimit = ItemOfSize("put-at-limit", "data", LimitBytes);
        await ddb.PutItemAsync(table, atLimit);
        var got = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("put-at-limit"))), ConsistentRead = true });
        var stored = got.Item?.GetValueOrDefault("data")?.S ?? string.Empty;
        Ensure(
            stored == atLimit["data"].S,
            $"a {LimitBytes}-UTF-8-byte multi-byte string item must round-trip whole through PutItem, got {Encoding.UTF8.GetByteCount(stored)} bytes back");

        // PutItem: one UTF-8 byte over is rejected and nothing is stored.
        var over = ItemOfSize("put-over-limit", "data", LimitBytes + 1);
        var (code, message) = await ErrorCodeAndMessage(async () => await ddb.PutItemAsync(table, over));
        Ensure(
            code == ErrValidation,
            $"a {LimitBytes + 1}-UTF-8-byte multi-byte string PutItem must be {ErrValidation}, got {Py.Repr(code)}: {Py.Repr(message)}");
        Ensure(
            message == "Item size has exceeded the maximum allowed size",
            $"expected AWS's exact ValidationException message, got {Py.Repr(message)}");
        var rejected = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("put-over-limit"))), ConsistentRead = true });
        Ensure(rejected.Item is null, $"a PutItem rejected for size must not persist, got {Py.Repr(rejected.Item)}");

        // BatchWriteItem PutRequest: exactly at the limit is accepted and round-trips.
        var bwAtLimit = ItemOfSize("batch-at-limit", "data", LimitBytes);
        var bwResp = await ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new() { [table] = [new WriteRequest { PutRequest = new PutRequest { Item = bwAtLimit } }] },
        });
        Ensure(
            bwResp.UnprocessedItems is null || bwResp.UnprocessedItems.Count == 0,
            $"a {LimitBytes}-UTF-8-byte multi-byte string BatchWriteItem PutRequest must fully process, got UnprocessedItems={Py.Repr(bwResp.UnprocessedItems)}");
        var bwGot = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("batch-at-limit"))), ConsistentRead = true });
        var bwStored = bwGot.Item?.GetValueOrDefault("data")?.S ?? string.Empty;
        Ensure(
            bwStored == bwAtLimit["data"].S,
            $"a {LimitBytes}-UTF-8-byte multi-byte string item must round-trip whole through BatchWriteItem, got {Encoding.UTF8.GetByteCount(bwStored)} bytes back");

        // BatchWriteItem PutRequest: one UTF-8 byte over is rejected and nothing is stored.
        var bwOver = ItemOfSize("batch-over-limit", "data", LimitBytes + 1);
        var (bwCode, bwMessage) = await ErrorCodeAndMessage(async () => await ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new() { [table] = [new WriteRequest { PutRequest = new PutRequest { Item = bwOver } }] },
        }));
        Ensure(
            bwCode == ErrValidation,
            $"a {LimitBytes + 1}-UTF-8-byte multi-byte string BatchWriteItem PutRequest must be {ErrValidation}, got {Py.Repr(bwCode)}: {Py.Repr(bwMessage)}");
        Ensure(
            bwMessage == "Item size has exceeded the maximum allowed size",
            $"expected AWS's exact ValidationException message, got {Py.Repr(bwMessage)}");
        var bwRejected = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("batch-over-limit"))), ConsistentRead = true });
        Ensure(bwRejected.Item is null, $"a BatchWriteItem PutRequest rejected for size must not persist, got {Py.Repr(bwRejected.Item)}");
    }
}

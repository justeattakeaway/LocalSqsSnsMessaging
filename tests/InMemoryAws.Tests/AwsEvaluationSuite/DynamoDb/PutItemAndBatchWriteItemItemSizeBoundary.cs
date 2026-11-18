// Translated from behavioral/dynamodb/check_put_item_and_batch_write_item_item_size_boundary.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.put-item-and-batch-write-item-item-size-boundary</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutItemAndBatchWriteItemItemSizeBoundary
{
    private const string CheckId = "put-item-and-batch-write-item-item-size-boundary";
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
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
    }

    private static string StoredString(GetItemResponse got) => got.Item?.GetValueOrDefault("data")?.S ?? "";

    /// <summary>
    /// PutItem and BatchWriteItem PutRequest accept an item at exactly the 409600-byte
    /// limit and reject one byte over with ValidationException, leaving nothing stored.
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ServiceQuotas.html#limits-items
    /// </summary>
    [Test]
    [DisplayName("dynamodb.put-item-and-batch-write-item-item-size-boundary")]
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

        // PutItem: exactly at the limit is accepted and reads back whole.
        var atLimit = ItemOfSize("put-at-limit", "data", LimitBytes);
        await ddb.PutItemAsync(table, atLimit);
        var got = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("put-at-limit"))), ConsistentRead = true });
        var stored = StoredString(got);
        Ensure(
            stored == atLimit["data"].S,
            $"a {LimitBytes}-byte item must round-trip whole through PutItem, got {stored.Length} bytes back");

        // PutItem: one byte over is rejected and nothing is stored.
        var over = ItemOfSize("put-over-limit", "data", LimitBytes + 1);
        var (code, message) = await ErrorCodeAndMessage(() => ddb.PutItemAsync(table, over));
        Ensure(
            code == ErrValidation,
            $"a {LimitBytes + 1}-byte PutItem must be {ErrValidation}, got {Py.Repr(code)}: {Py.Repr(message)}");
        Ensure(
            message == "Item size has exceeded the maximum allowed size",
            $"expected AWS's exact ValidationException message, got {Py.Repr(message)}");
        var rejected = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("put-over-limit"))), ConsistentRead = true });
        Ensure(rejected.Item is null, $"a PutItem rejected for size must not persist, got {Py.Repr(rejected.Item)}");

        // BatchWriteItem PutRequest: exactly at the limit is accepted and reads back whole.
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
            $"a {LimitBytes}-byte BatchWriteItem PutRequest must fully process, got UnprocessedItems={Py.Repr(bwResp.UnprocessedItems)}");
        var bwGot = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("batch-at-limit"))), ConsistentRead = true });
        var bwStored = StoredString(bwGot);
        Ensure(
            bwStored == bwAtLimit["data"].S,
            $"a {LimitBytes}-byte item must round-trip whole through BatchWriteItem, got {bwStored.Length} bytes back");

        // BatchWriteItem PutRequest: one byte over is rejected and nothing is stored.
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
            $"a {LimitBytes + 1}-byte BatchWriteItem PutRequest must be {ErrValidation}, got {Py.Repr(bwCode)}: {Py.Repr(bwMessage)}");
        Ensure(
            bwMessage == "Item size has exceeded the maximum allowed size",
            $"expected AWS's exact ValidationException message, got {Py.Repr(bwMessage)}");
        var bwRejected = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("batch-over-limit"))), ConsistentRead = true });
        Ensure(bwRejected.Item is null, $"a BatchWriteItem PutRequest rejected for size must not persist, got {Py.Repr(bwRejected.Item)}");
    }
}

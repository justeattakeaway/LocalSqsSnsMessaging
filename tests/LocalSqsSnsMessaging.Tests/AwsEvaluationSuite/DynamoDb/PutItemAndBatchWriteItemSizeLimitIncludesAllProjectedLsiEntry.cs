// Translated from behavioral/dynamodb/check_put_item_and_batch_write_item_size_limit_includes_all_projected_lsi_entry.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.put-item-and-batch-write-item-size-limit-includes-all-projected-lsi-entry</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutItemAndBatchWriteItemSizeLimitIncludesAllProjectedLsiEntry
{
    private const string CheckId = "put-item-and-batch-write-item-size-limit-includes-all-projected-lsi-entry";
    private static readonly (string Code, string Message) OverLimit = ("ValidationException", "Item size has exceeded the maximum allowed size");
    private const int ItemBytes = 390000;

    /// <summary>A pk/sk/li-keyed item whose aggregate encoded size is exactly <paramref name="totalBytes"/>.</summary>
    private static Dictionary<string, AttributeValue> ItemOfSize(string pkValue, int totalBytes)
    {
        var overhead =
            "pk".Length + Encoding.UTF8.GetByteCount(pkValue)
            + "sk".Length + "1".Length
            + "li".Length + "idx".Length
            + "data".Length;
        var fillerLen = totalBytes - overhead;
        Ensure(fillerLen > 0, $"{totalBytes} bytes is too small for this item shape");
        return Item(
            ("pk", S(pkValue)),
            ("sk", N("1")),
            ("li", S("idx")),
            ("data", S(new string('x', fillerLen))));
    }

    /// <summary>(code, message) of the error <paramref name="fn"/> raises; raises if the call unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task<object>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }

    private static async Task ExpectError(Func<Task<object>> fn, (string Code, string Message) expected)
    {
        var (gotCode, gotMessage) = await Error(fn);
        Ensure(gotCode == expected.Code, $"expected {Py.Repr(expected.Code)}, got {Py.Repr(gotCode)} ({Py.Repr(gotMessage)})");
        Ensure(gotMessage == expected.Message, $"unexpected message {Py.Repr(gotMessage)}");
    }

    /// <summary>
    /// PutItem and BatchWriteItem PutRequest size a write against the item plus its ALL-projected
    /// local secondary index entry combined: a ~390 KB item is accepted with no local secondary
    /// index but refused ValidationException with one, storing nothing either way.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.put-item-and-batch-write-item-size-limit-includes-all-projected-lsi-entry")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var plainTable = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = plainTable,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.N)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(plainTable));

        var lsiTable = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = lsiTable,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.N),
                new AttributeDefinition("li", ScalarAttributeType.S),
            ],
            LocalSecondaryIndexes =
            [
                new LocalSecondaryIndex
                {
                    IndexName = "lsi1",
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("li", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(lsiTable));

        await ddb.WaitForTableExists(plainTable, delaySeconds: 2, maxAttempts: 90);
        await ddb.WaitForTableExists(lsiTable, delaySeconds: 2, maxAttempts: 90);

        // Positive control: the same ~390 KB item is under the 400 KB ceiling on its own and is
        // accepted and reads back whole on a table with no local secondary index.
        var plainItem = ItemOfSize("put-plain", ItemBytes);
        await ddb.PutItemAsync(plainTable, plainItem);
        var got = (await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = plainTable,
            Key = Item(("pk", S("put-plain")), ("sk", N("1"))),
            ConsistentRead = true,
        })).Item ?? [];
        Ensure(
            got.GetValueOrDefault("data")?.S == plainItem["data"].S,
            $"a {ItemBytes}-byte item must round-trip whole through PutItem, got {(got.GetValueOrDefault("data")?.S ?? string.Empty).Length} bytes back");

        // PutItem: the same-sized item, now with an ALL-projected local secondary index mirroring it,
        // is refused — the combined (base item + index entry) size exceeds the 400 KB ceiling.
        var putItem = ItemOfSize("put-lsi", ItemBytes);
        await ExpectError(async () => await ddb.PutItemAsync(lsiTable, putItem), OverLimit);
        var rejected = await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = lsiTable,
            Key = Item(("pk", S("put-lsi")), ("sk", N("1"))),
            ConsistentRead = true,
        });
        Ensure(rejected.Item is null, $"a PutItem rejected for size must not persist, got {Py.Repr(rejected.Item)}");

        // BatchWriteItem PutRequest: the same combined-size rule applies.
        var batchItem = ItemOfSize("batch-lsi", ItemBytes);
        await ExpectError(
            async () => await ddb.BatchWriteItemAsync(new BatchWriteItemRequest
            {
                RequestItems = new() { [lsiTable] = [new WriteRequest { PutRequest = new PutRequest { Item = batchItem } }] },
            }),
            OverLimit);
        var batchRejected = await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = lsiTable,
            Key = Item(("pk", S("batch-lsi")), ("sk", N("1"))),
            ConsistentRead = true,
        });
        Ensure(batchRejected.Item is null, $"a BatchWriteItem PutRequest rejected for size must not persist, got {Py.Repr(batchRejected.Item)}");
    }
}

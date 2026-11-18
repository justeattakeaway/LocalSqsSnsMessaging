// Translated from behavioral/dynamodb/check_item_size_boundary_across_multiple_long_attribute_names.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Text;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.item-size-boundary-across-multiple-long-attribute-names</summary>
[Category("AwsEvaluationSuite")]
public sealed class ItemSizeBoundaryAcrossMultipleLongAttributeNames
{
    private const string CheckId = "item-size-boundary-across-multiple-long-attribute-names";
    private const string ErrValidation = "ValidationException";
    private const string ErrMessage = "Item size has exceeded the maximum allowed size";
    private const int LimitBytes = 409600;

    /// <summary>A unique, exactly-27-character attribute name.</summary>
    private static string AttrName(int i)
    {
        var prefix = $"payload-attr-{i.ToString("00", CultureInfo.InvariantCulture)}-";
        var name = prefix + new string('n', 27 - prefix.Length);
        Ensure(name.Length == 27, $"helper built a {name.Length}-char name, expected 27: {Py.Repr(name)}");
        return name;
    }

    /// <summary>
    /// An item whose <c>pk</c> plus every attribute in <paramref name="names"/> sums to exactly totalBytes,
    /// counting each attribute's name and value length.
    /// </summary>
    private static Dictionary<string, AttributeValue> ItemOfSize(string pkValue, List<string> names, int totalBytes)
    {
        var fixedBytes = Encoding.UTF8.GetByteCount("pk") + Encoding.UTF8.GetByteCount(pkValue) + names.Sum(Encoding.UTF8.GetByteCount);
        var payloadBytes = totalBytes - fixedBytes;
        var n = names.Count;
        Ensure(payloadBytes >= n, $"{totalBytes} bytes is too small for {n} named attributes");
        var (baseLength, extra) = Math.DivRem(payloadBytes, n);
        var item = Item(("pk", S(pkValue)));
        for (var idx = 0; idx < names.Count; idx++)
        {
            var length = baseLength + (idx == 0 ? extra : 0);
            item[names[idx]] = S(new string('x', length));
        }

        return item;
    }

    /// <summary>The Error Code and Message <c>fn</c> raises; an unexpected success raises too.</summary>
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

    private static List<string> SortedKeys(IEnumerable<string> keys) => [.. keys.Order(StringComparer.Ordinal)];

    /// <summary>
    /// Each attribute name's byte length is summed into the item size alongside its value:
    /// the 409600/409601 boundary holds byte-exact with 2 and 10 payload attributes named 27
    /// characters each, for both PutItem and BatchWriteItem PutRequest.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.item-size-boundary-across-multiple-long-attribute-names")]
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

        var names2 = Enumerable.Range(0, 2).Select(AttrName).ToList();
        var names10 = Enumerable.Range(0, 10).Select(AttrName).ToList();

        // PutItem, 2 payload attributes: exactly at the limit is accepted and reads back whole,
        // byte-exact in every attribute including its 27-character name.
        var atItem = ItemOfSize("put-2attr-at-limit", names2, LimitBytes);
        await ddb.PutItemAsync(table, atItem);
        var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("put-2attr-at-limit"))), ConsistentRead = true })).Item ?? [];
        Ensure(
            SortedKeys(got.Keys).SequenceEqual(SortedKeys(names2.Prepend("pk"))),
            $"a {LimitBytes}-byte item across 2 named attributes must read back all of them, got {Py.Repr(SortedKeys(got.Keys))}");
        Ensure(names2.All(n => Py.Equal(got[n], atItem[n])), $"every 27-char-named attribute must read back byte-identical, got {Py.Repr(got)}");

        // PutItem, 2 payload attributes: one byte over is rejected and nothing is stored.
        var overItem = ItemOfSize("put-2attr-over-limit", names2, LimitBytes + 1);
        var (code, message) = await ErrorCodeAndMessage(() => ddb.PutItemAsync(table, overItem));
        Ensure(code == ErrValidation, $"a {LimitBytes + 1}-byte PutItem across 2 named attributes must be {ErrValidation}, got {Py.Repr(code)}");
        Ensure(message == ErrMessage, $"expected AWS's exact ValidationException message, got {Py.Repr(message)}");
        var rejected = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("put-2attr-over-limit"))), ConsistentRead = true });
        Ensure(rejected.Item is null, $"a PutItem rejected for size must not persist, got {Py.Repr(rejected.Item)}");

        // BatchWriteItem PutRequest, 10 payload attributes: exactly at the limit is accepted and
        // reads back whole.
        var atItem10 = ItemOfSize("batch-10attr-at-limit", names10, LimitBytes);
        var bwResp = await ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new Dictionary<string, List<WriteRequest>>(StringComparer.Ordinal) { [table] = [new WriteRequest { PutRequest = new PutRequest { Item = atItem10 } }] },
        });
        Ensure(
            (bwResp.UnprocessedItems ?? []).Count == 0,
            $"a {LimitBytes}-byte BatchWriteItem PutRequest across 10 named attributes must fully process, got UnprocessedItems={Py.Repr(bwResp.UnprocessedItems)}");
        var got10 = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("batch-10attr-at-limit"))), ConsistentRead = true })).Item ?? [];
        Ensure(
            SortedKeys(got10.Keys).SequenceEqual(SortedKeys(names10.Prepend("pk"))),
            $"a {LimitBytes}-byte item across 10 named attributes must read back all of them, got {Py.Repr(SortedKeys(got10.Keys))}");
        Ensure(names10.All(n => Py.Equal(got10[n], atItem10[n])), $"every 27-char-named attribute must read back byte-identical, got {Py.Repr(got10)}");

        // BatchWriteItem PutRequest, 10 payload attributes: one byte over is rejected and nothing
        // is stored.
        var overItem10 = ItemOfSize("batch-10attr-over-limit", names10, LimitBytes + 1);
        var (bwCode, bwMessage) = await ErrorCodeAndMessage(() => ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new Dictionary<string, List<WriteRequest>>(StringComparer.Ordinal) { [table] = [new WriteRequest { PutRequest = new PutRequest { Item = overItem10 } }] },
        }));
        Ensure(bwCode == ErrValidation, $"a {LimitBytes + 1}-byte BatchWriteItem PutRequest across 10 named attributes must be {ErrValidation}, got {Py.Repr(bwCode)}");
        Ensure(bwMessage == ErrMessage, $"expected AWS's exact ValidationException message, got {Py.Repr(bwMessage)}");
        var bwRejected = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("batch-10attr-over-limit"))), ConsistentRead = true });
        Ensure(bwRejected.Item is null, $"a BatchWriteItem PutRequest rejected for size must not persist, got {Py.Repr(bwRejected.Item)}");
    }
}

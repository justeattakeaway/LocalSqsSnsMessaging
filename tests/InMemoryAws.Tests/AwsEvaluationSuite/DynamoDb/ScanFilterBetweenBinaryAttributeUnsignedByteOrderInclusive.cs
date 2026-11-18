// Translated from behavioral/dynamodb/check_scan_filter_between_binary_attribute_unsigned_byte_order_inclusive.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-filter-between-binary-attribute-unsigned-byte-order-inclusive</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanFilterBetweenBinaryAttributeUnsignedByteOrderInclusive
{
    private const string Check = "scan-filter-between-binary-attribute-unsigned-byte-order-inclusive";

    // Five one-byte Binary values spanning the signed/unsigned boundary at 0x80. Every expected set
    // below is derived from this seed at run time, never hardcoded.
    private static readonly (string Id, byte[] Value)[] Rows =
    [
        ("item0", [0x00]),
        ("item1", [0x01]),
        ("item2", [0x7f]),
        ("item3", [0x80]),
        ("item4", [0xff]),
    ];

    /// <summary>Python's <c>bytes</c> comparison: lexicographic over unsigned bytes.</summary>
    private static int Compare(byte[] a, byte[] b) => ((ReadOnlySpan<byte>)a).SequenceCompareTo(b);

    /// <summary>
    /// A Scan FilterExpression BETWEEN on a Binary attribute compares unsigned bytes and
    /// includes both bounds: 0x80 ranks above 0x01, which a signed comparison would invert.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-filter-between-binary-attribute-unsigned-byte-order-inclusive")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await Seed(ctx, ddb);

        static List<string> Want(byte[] lo, byte[] hi) =>
            [.. Rows.Where(r => Compare(lo, r.Value) <= 0 && Compare(r.Value, hi) <= 0).Select(r => r.Id).Order(StringComparer.Ordinal)];

        (byte[] Lo, byte[] Hi)[] cases =
        [
            ([0x01], [0x80]), // spans the signed/unsigned boundary; only valid in unsigned order
            ([0x80], [0xff]), // both bounds in the "negative" half under a signed reading
            ([0x00], [0xff]), // the full range: every item, both bounds inclusive
            ([0x01], [0x7f]), // neither bound touches the boundary byte
        ];
        var sizes = cases.Select(c => Want(c.Lo, c.Hi).Count).ToList();
        Ensure(sizes.SequenceEqual([3, 2, 5, 2]), $"the seed-derived expectations moved: {Py.Repr(sizes)}");
        foreach (var (lo, hi) in cases)
        {
            var got = Ids(await ScanAll(ddb, table, "bin BETWEEN :lo AND :hi", Item((":lo", B(lo)), (":hi", B(hi)))));
            var expected = Want(lo, hi);
            Ensure(got.SequenceEqual(expected), $"bin BETWEEN {Py.Repr(lo)} AND {Py.Repr(hi)} returned {Py.Repr(got)}, expected {Py.Repr(expected)}");
        }

        // Positive control: the exact bound values are themselves matched (inclusive on both ends).
        {
            byte[] lo = [0x01];
            byte[] hi = [0x80];
            var got = Ids(await ScanAll(ddb, table, "bin BETWEEN :lo AND :hi", Item((":lo", B(lo)), (":hi", B(hi)))));
            Ensure(
                got.Contains("item1") && got.Contains("item3"),
                $"BETWEEN {Py.Repr(lo)} AND {Py.Repr(hi)} excluded an item equal to one of its own bounds: {Py.Repr(got)}");
        }
    }

    /// <summary>A fresh on-demand table keyed by <c>id</c>, seeded with the Binary rows in one BatchWriteItem.</summary>
    private static async Task<string> Seed(FidelityContext ctx, IAmazonDynamoDB ddb)
    {
        var table = FidelityContext.UniqueName("sim2real-ddbscb-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
            KeySchema = [new KeySchemaElement("id", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("id", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        var items = Rows.Select(r => Item(("id", S(r.Id)), ("bin", B(r.Value)))).ToList();
        var resp = await ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new() { [table] = [.. items.Select(i => new WriteRequest { PutRequest = new PutRequest { Item = i } })] },
        });
        Ensure((resp.UnprocessedItems ?? []).Count == 0, $"seeding BatchWriteItem left {Py.Repr(resp.UnprocessedItems)} unprocessed");
        return table;
    }

    private static List<string> Ids(IEnumerable<Dictionary<string, AttributeValue>> items) => [.. items.Select(i => i["id"].S).Order(StringComparer.Ordinal)];

    /// <summary>Every item of a strongly consistent scan, across pages.</summary>
    private static async Task<List<Dictionary<string, AttributeValue>>> ScanAll(
        IAmazonDynamoDB ddb, string table, string filterExpression, Dictionary<string, AttributeValue> values)
    {
        var items = new List<Dictionary<string, AttributeValue>>();
        Dictionary<string, AttributeValue>? start = null;
        while (true)
        {
            var page = await ddb.ScanAsync(new ScanRequest
            {
                ConsistentRead = true,
                TableName = table,
                FilterExpression = filterExpression,
                ExpressionAttributeValues = values,
                ExclusiveStartKey = start,
            });
            items.AddRange(page.Items);
            start = page.LastEvaluatedKey;
            if (start is null || start.Count == 0)
            {
                return items;
            }
        }
    }
}

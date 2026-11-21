// Translated from behavioral/dynamodb/check_scan_filter_le_lt_ge_binary_attribute_and_legacy_between_unsigned_byte_order.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-filter-le-lt-ge-binary-attribute-and-legacy-between-unsigned-byte-order</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanFilterLeLtGeBinaryAttributeAndLegacyBetweenUnsignedByteOrder
{
    private const string Check = "scan-filter-le-lt-ge-binary-attribute-and-legacy-between-unsigned-byte-order";

    // Five one-byte Binary values spanning the signed/unsigned boundary at 0x80. Every expected set
    // below is derived from this seed at run time, never hardcoded.
    private static readonly (string Id, byte[] Val)[] Rows =
    [
        ("item0", [0x00]),
        ("item1", [0x01]),
        ("item2", [0x7f]),
        ("item3", [0x80]),
        ("item4", [0xff]),
    ];

    private static readonly byte[] Bound = [0x80];

    /// <summary>Python's bytes comparison: lexicographic over unsigned bytes.</summary>
    private static int Cmp(byte[] a, byte[] b) => a.AsSpan().SequenceCompareTo(b.AsSpan());

    private static List<string> Want(Func<byte[], bool> keep) =>
        Rows.Where(r => keep(r.Val)).Select(r => r.Id).Order(StringComparer.Ordinal).ToList();

    /// <summary>
    /// A Scan FilterExpression and the legacy ScanFilter compare a Binary attribute as unsigned
    /// bytes for &lt;=, &lt; and &gt;=, and the legacy ScanFilter's BETWEEN does the same across the boundary.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-filter-le-lt-ge-binary-attribute-and-legacy-between-unsigned-byte-order")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await Seed(ctx, ddb);

        var leExpected = Want(v => Cmp(v, Bound) <= 0);
        var ltExpected = Want(v => Cmp(v, Bound) < 0);
        var geExpected = Want(v => Cmp(v, Bound) >= 0);
        Ensure(leExpected.SequenceEqual(["item0", "item1", "item2", "item3"]), $"the seed-derived LE expectation moved: {Py.Repr(leExpected)}");
        Ensure(ltExpected.SequenceEqual(["item0", "item1", "item2"]), $"the seed-derived LT expectation moved: {Py.Repr(ltExpected)}");
        Ensure(geExpected.SequenceEqual(["item3", "item4"]), $"the seed-derived GE expectation moved: {Py.Repr(geExpected)}");

        var feLe = Ids(await ScanAll(ddb, new ScanRequest
        {
            TableName = table,
            FilterExpression = "bin <= :v",
            ExpressionAttributeValues = Item((":v", B(Bound))),
        }));
        Ensure(
            feLe.SequenceEqual(leExpected),
            $"FilterExpression 'bin <= 0x80' returned {Py.Repr(feLe)}, expected {Py.Repr(leExpected)} under " +
            "unsigned byte order");
        var legacyLe = Ids(await ScanAll(ddb, new ScanRequest { TableName = table, ScanFilter = Legacy(ComparisonOperator.LE, B(Bound)) }));
        Ensure(
            legacyLe.SequenceEqual(feLe),
            $"legacy ScanFilter LE on bin returned {Py.Repr(legacyLe)}, must match FilterExpression's {Py.Repr(feLe)}");

        var feLt = Ids(await ScanAll(ddb, new ScanRequest
        {
            TableName = table,
            FilterExpression = "bin < :v",
            ExpressionAttributeValues = Item((":v", B(Bound))),
        }));
        Ensure(
            feLt.SequenceEqual(ltExpected),
            $"FilterExpression 'bin < 0x80' returned {Py.Repr(feLt)}, expected {Py.Repr(ltExpected)}: the boundary " +
            "item itself must be excluded");
        var legacyLt = Ids(await ScanAll(ddb, new ScanRequest { TableName = table, ScanFilter = Legacy(ComparisonOperator.LT, B(Bound)) }));
        Ensure(
            legacyLt.SequenceEqual(feLt),
            $"legacy ScanFilter LT on bin returned {Py.Repr(legacyLt)}, must match FilterExpression's {Py.Repr(feLt)}");

        var feGe = Ids(await ScanAll(ddb, new ScanRequest
        {
            TableName = table,
            FilterExpression = "bin >= :v",
            ExpressionAttributeValues = Item((":v", B(Bound))),
        }));
        Ensure(
            feGe.SequenceEqual(geExpected),
            $"FilterExpression 'bin >= 0x80' returned {Py.Repr(feGe)}, expected {Py.Repr(geExpected)}: 0x80 and " +
            "0xff rank above 0x7f under unsigned byte order, not below");
        var legacyGe = Ids(await ScanAll(ddb, new ScanRequest { TableName = table, ScanFilter = Legacy(ComparisonOperator.GE, B(Bound)) }));
        Ensure(
            legacyGe.SequenceEqual(feGe),
            $"legacy ScanFilter GE on bin returned {Py.Repr(legacyGe)}, must match FilterExpression's {Py.Repr(feGe)}");

        byte[] lo = [0x01], hi = [0x80];
        var betweenExpected = Want(v => Cmp(lo, v) <= 0 && Cmp(v, hi) <= 0);
        Ensure(betweenExpected.SequenceEqual(["item1", "item2", "item3"]), $"the seed-derived BETWEEN expectation moved: {Py.Repr(betweenExpected)}");
        var legacyBetween = Ids(await ScanAll(ddb, new ScanRequest
        {
            TableName = table,
            ScanFilter = new Dictionary<string, Condition>
            {
                ["bin"] = new() { ComparisonOperator = ComparisonOperator.BETWEEN, AttributeValueList = [B(lo), B(hi)] },
            },
        }));
        Ensure(
            legacyBetween.SequenceEqual(betweenExpected),
            $"legacy ScanFilter BETWEEN 0x01..0x80 on bin returned {Py.Repr(legacyBetween)}, expected " +
            $"{Py.Repr(betweenExpected)} under unsigned byte order");
        var feBetween = Ids(await ScanAll(ddb, new ScanRequest
        {
            TableName = table,
            FilterExpression = "bin BETWEEN :lo AND :hi",
            ExpressionAttributeValues = Item((":lo", B(lo)), (":hi", B(hi))),
        }));
        Ensure(
            feBetween.SequenceEqual(legacyBetween),
            $"FilterExpression BETWEEN 0x01..0x80 returned {Py.Repr(feBetween)}, must match legacy " +
            $"ScanFilter's {Py.Repr(legacyBetween)}");

        // Positive control: both inclusive bounds are themselves selected by LE/GE/BETWEEN.
        Ensure(
            feLe.Contains("item3") && feGe.Contains("item3"),
            $"the boundary value 0x80 was excluded from an inclusive comparison: LE={Py.Repr(feLe)} " +
            $"GE={Py.Repr(feGe)}");
        Ensure(
            legacyBetween.Contains("item1") && legacyBetween.Contains("item3"),
            $"BETWEEN 0x01..0x80 excluded one of its own bounds: {Py.Repr(legacyBetween)}");
    }

    private static Dictionary<string, Condition> Legacy(ComparisonOperator op, AttributeValue value) => new()
    {
        ["bin"] = new() { ComparisonOperator = op, AttributeValueList = [value] },
    };

    /// <summary>A fresh on-demand table keyed by <c>id</c>, seeded with the Binary rows in one BatchWriteItem.</summary>
    private static async Task<string> Seed(FidelityContext ctx, IAmazonDynamoDB ddb)
    {
        var table = FidelityContext.UniqueName("sim2real-ddbscle-");
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
        var items = Rows.Select(r => Item(("id", S(r.Id)), ("bin", B(r.Val)))).ToList();
        var resp = await ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new Dictionary<string, List<WriteRequest>>
            {
                [table] = [.. items.Select(i => new WriteRequest { PutRequest = new PutRequest { Item = i } })],
            },
        });
        Ensure(
            (resp.UnprocessedItems ?? []).Count == 0,
            $"seeding BatchWriteItem left {Py.Repr(resp.UnprocessedItems)} unprocessed");
        return table;
    }

    private static List<string> Ids(IEnumerable<Dictionary<string, AttributeValue>> items) =>
        items.Select(i => i["id"].S).Order(StringComparer.Ordinal).ToList();

    /// <summary>Every item of a strongly consistent scan, across pages.</summary>
    private static async Task<List<Dictionary<string, AttributeValue>>> ScanAll(IAmazonDynamoDB ddb, ScanRequest request)
    {
        var items = new List<Dictionary<string, AttributeValue>>();
        Dictionary<string, AttributeValue>? start = null;
        while (true)
        {
            request.ConsistentRead = true;
            request.ExclusiveStartKey = start is { Count: > 0 } ? start : null;
            var page = await ddb.ScanAsync(request);
            items.AddRange(page.Items ?? []);
            start = page.LastEvaluatedKey;
            if (start is not { Count: > 0 })
            {
                return items;
            }
        }
    }
}

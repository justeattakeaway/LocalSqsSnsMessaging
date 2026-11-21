// Translated from behavioral/dynamodb/check_scan_1mb_page_boundary_on_a_hash_only_table_with_small_attributes.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-1mb-page-boundary-on-a-hash-only-table-with-small-attributes</summary>
[Category("AwsEvaluationSuite")]
public sealed class Scan1mbPageBoundaryOnAHashOnlyTableWithSmallAttributes
{
    private const string Check = "scan-1mb-page-boundary-on-a-hash-only-table-with-small-attributes";
    private const int OneMb = 1024 * 1024;
    private const int PayloadBytes = 50 * 1024;
    private const int UnderCount = 10;
    private const int OverCount = 30;
    private const int MaxPages = 20;

    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };

    private static Dictionary<string, AttributeValue> MakeItem(int i) =>
        Item(("pk", S($"item-{i:D4}")), ("v", S(new string('x', PayloadBytes))));

    /// <summary>Item size for an all-string item: UTF-8 length of each name plus its value.</summary>
    private static int ItemBytes(Dictionary<string, AttributeValue> item) =>
        item.Sum(e => Encoding.UTF8.GetByteCount(e.Key) + Encoding.UTF8.GetByteCount(e.Value.S));

    private static async Task WaitActive(IAmazonDynamoDB ddb, string table)
    {
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(270.0))
        {
            if ((await ddb.DescribeTableAsync(table)).Table.TableStatus == TableStatus.ACTIVE)
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {Py.Repr(table)} did not become ACTIVE before the deadline");
    }

    private static async Task BatchPut(IAmazonDynamoDB ddb, string table, List<Dictionary<string, AttributeValue>> items)
    {
        for (var start = 0; start < items.Count; start += 25)
        {
            var pending = new Dictionary<string, List<WriteRequest>>(StringComparer.Ordinal)
            {
                [table] = [.. items.Skip(start).Take(25).Select(it => new WriteRequest { PutRequest = new PutRequest { Item = it } })],
            };
            while (true)
            {
                pending = (await ddb.BatchWriteItemAsync(new BatchWriteItemRequest { RequestItems = pending })).UnprocessedItems ?? [];
                if (pending.Count == 0)
                {
                    break;
                }
            }
        }
    }

    /// <summary>Every Scan page with no Limit, following LastEvaluatedKey.</summary>
    private static async Task<List<ScanResponse>> Walk(IAmazonDynamoDB ddb, string table)
    {
        var pages = new List<ScanResponse>();
        Dictionary<string, AttributeValue>? start = null;
        while (true)
        {
            var resp = await ddb.ScanAsync(new ScanRequest
            {
                TableName = table,
                ConsistentRead = true,
                ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
                ExclusiveStartKey = start,
            });
            pages.Add(resp);
            start = resp.LastEvaluatedKey;
            if (start is null || start.Count == 0)
            {
                return pages;
            }

            Ensure(pages.Count < MaxPages, $"Scan did not end after {MaxPages} pages");
        }
    }

    /// <summary>
    /// A no-Limit Scan over many hash keys truncates each page at the shortest item prefix
    /// exceeding 1 MB, Count equals ScannedCount with no filter, and every item is returned exactly
    /// once across the pages.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-1mb-page-boundary-on-a-hash-only-table-with-small-attributes")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbscanhash-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await WaitActive(ddb, table);

        // Phase 1: 10 items of 50 KB (500 KB total) sit well under the 1 MB budget.
        await BatchPut(ddb, table, [.. Enumerable.Range(0, UnderCount).Select(MakeItem)]);
        var resp1 = await ddb.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = true, ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL });
        Ensure(
            resp1.LastEvaluatedKey is null,
            $"{UnderCount} items of {PayloadBytes} bytes each must fit on one page; got LastEvaluatedKey {Py.Repr(resp1.LastEvaluatedKey)}");
        Ensure(
            resp1.Count == resp1.ScannedCount && resp1.ScannedCount == UnderCount && resp1.Items.Count == UnderCount,
            $"with no FilterExpression Count must equal ScannedCount and the item count; got Count={Py.Repr(resp1.Count)} ScannedCount={Py.Repr(resp1.ScannedCount)} items={Py.Repr(resp1.Items.Count)}");
        var strong1 = resp1.ConsumedCapacity.CapacityUnits;
        var eventual1 = (await ddb.ScanAsync(new ScanRequest { TableName = table, ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL })).ConsumedCapacity.CapacityUnits;
        Ensure(
            strong1 == 2 * eventual1 && strong1 > 0,
            $"a strongly consistent scan must cost exactly twice the eventually consistent one; got strong={Py.Repr(strong1)} eventual={Py.Repr(eventual1)}");

        // Phase 2: grow to 30 items (1.5 MB total), forcing at least one truncated page.
        await BatchPut(ddb, table, [.. Enumerable.Range(UnderCount, OverCount - UnderCount).Select(MakeItem)]);
        var pages = await Walk(ddb, table);
        Ensure(
            pages.Count >= 2,
            $"{OverCount} items of {PayloadBytes} bytes each ({OverCount * PayloadBytes} bytes) must not fit on one page; got {pages.Count} page(s)");
        for (var n = 1; n <= pages.Count; n++)
        {
            var page = pages[n - 1];
            Ensure(
                page.Count == page.ScannedCount && page.ScannedCount == page.Items.Count,
                $"page {n}: Count={Py.Repr(page.Count)} ScannedCount={Py.Repr(page.ScannedCount)} for {Py.Repr(page.Items.Count)} items");
            if (page.LastEvaluatedKey is not null)
            {
                var items = page.Items;
                var total = items.Sum(ItemBytes);
                var withoutLast = total - ItemBytes(items[^1]);
                Ensure(
                    total > OneMb,
                    $"page {n} carries a LastEvaluatedKey, so it was truncated by the 1 MB budget and must exceed it; got {total} bytes over {items.Count} items");
                Ensure(
                    withoutLast <= OneMb,
                    $"page {n} must be the SHORTEST prefix exceeding 1 MB; dropping its last item still leaves {withoutLast} bytes");
            }
        }

        var seen = pages.SelectMany(page => page.Items).Select(it => it["pk"].S).ToList();
        var expected = Enumerable.Range(0, OverCount).Select(i => $"item-{i:D4}").Order(StringComparer.Ordinal).ToList();
        var distinct = seen.ToHashSet(StringComparer.Ordinal).Count;
        Ensure(
            seen.Order(StringComparer.Ordinal).SequenceEqual(expected) && seen.Count == distinct,
            $"paged Scan must return each of the {OverCount} items exactly once; got {seen.Count} rows, {distinct} distinct");
    }
}

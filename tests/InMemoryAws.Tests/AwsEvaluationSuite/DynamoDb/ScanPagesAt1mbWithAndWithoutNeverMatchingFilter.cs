// Translated from behavioral/dynamodb/check_scan_pages_at_1mb_with_and_without_never_matching_filter.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-pages-at-1mb-with-and-without-never-matching-filter</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanPagesAt1mbWithAndWithoutNeverMatchingFilter
{
    private const int OneMb = 1024 * 1024;
    private const int Big = 26;
    private const int Tiny = 26;
    private const int Payload = 50 * 1024;
    private const int MaxPages = 20;

    /// <summary>A cleanup running undo() where a failed delete raises; in-use is retried every 2 s.</summary>
    private static Func<Task> Teardown(Func<Task> undo, string[] notFound, double timeoutS = 300.0) => async () =>
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(undo);
            if (error is null || notFound.Contains(error.Code))
            {
                return;
            }

            if (error.Code != "ResourceInUseException" || clock.Elapsed.TotalSeconds >= timeoutS)
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    };

    /// <summary>Poll DescribeTable every 2 s until the table and its indexes are ACTIVE.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var left = ctx.TimeLeftSeconds;
        var clock = FidelityTime.StartNew();
        var deadline = TimeSpan.FromSeconds(Math.Max(0.0, (left == 0 ? 300.0 : left) - 30.0));
        TableDescription? desc = null;
        while (clock.Elapsed < deadline)
        {
            desc = (await ddb.DescribeTableAsync(table)).Table;
            if (desc.TableStatus == TableStatus.ACTIVE && (desc.GlobalSecondaryIndexes ?? []).All(g => g.IndexStatus == IndexStatus.ACTIVE))
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {Py.Repr(desc)}");
    }

    /// <summary>BatchWriteItem in chunks of 25, re-sending UnprocessedItems every 2 s until none remain.</summary>
    private static async Task BatchPut(IAmazonDynamoDB ddb, FidelityContext ctx, string table, List<Dictionary<string, AttributeValue>> items)
    {
        var left = ctx.TimeLeftSeconds;
        var clock = FidelityTime.StartNew();
        var deadline = TimeSpan.FromSeconds(Math.Max(0.0, (left == 0 ? 300.0 : left) - 60.0));
        for (var start = 0; start < items.Count; start += 25)
        {
            Dictionary<string, List<WriteRequest>> pending = new()
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

                Ensure(clock.Elapsed < deadline, $"BatchWriteItem left {Py.Repr(pending.Values.Sum(v => v.Count))} items unprocessed");
                await FidelityTime.Delay(TimeSpan.FromSeconds(2));
            }
        }
    }

    /// <summary>Item size for an all-string item: UTF-8 length of each name plus its value.</summary>
    private static int ItemBytes(Dictionary<string, AttributeValue> item) =>
        item.Sum(e => Encoding.UTF8.GetByteCount(e.Key) + Encoding.UTF8.GetByteCount(e.Value.S));

    /// <summary>Every Scan page with no Limit, following LastEvaluatedKey.</summary>
    private static async Task<List<ScanResponse>> Walk(IAmazonDynamoDB ddb, string table, string? filterExpression = null)
    {
        var pages = new List<ScanResponse>();
        var request = new ScanRequest { TableName = table, ConsistentRead = true, FilterExpression = filterExpression };
        while (true)
        {
            var resp = await ddb.ScanAsync(request);
            pages.Add(resp);
            if (resp.LastEvaluatedKey is null)
            {
                return pages;
            }

            Ensure(pages.Count < MaxPages, $"Scan did not end after {MaxPages} pages");
            request.ExclusiveStartKey = resp.LastEvaluatedKey;
        }
    }

    /// <summary>
    /// Scan with no Limit over ~1.3 MB stops each page at 1 MB (within one item) and returns every
    /// item once; with a never-matching filter the pages are empty but keep a LastEvaluatedKey.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Scan.html#Scan.Pagination
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-pages-at-1mb-with-and-without-never-matching-filter")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-scan1mb-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), ["ResourceNotFoundException"]));
        await WaitActive(ddb, ctx, table);
        var items = new List<Dictionary<string, AttributeValue>>();
        for (var i = 0; i < Big + Tiny; i++)
        {
            var payload = i % 2 == 0 ? new string('x', Payload) : "t";
            items.Add(Item(("pk", S($"i{i:D3}")), ("payload", S(payload))));
        }

        await BatchPut(ddb, ctx, table, items);
        var expected = items.Select(it => it["pk"].S).Order(StringComparer.Ordinal).ToList();
        var total = items.Sum(ItemBytes);
        var largest = items.Max(ItemBytes);
        var pages = await Walk(ddb, table);
        var sizes = pages.Select(p => p.Items.Sum(ItemBytes)).ToList();
        Ensure(
            pages.Count >= 2 && pages[0].LastEvaluatedKey is not null,
            $"a Scan over {total} bytes must end its first page with a LastEvaluatedKey; page sizes {Py.Repr(sizes)}");
        for (var n = 1; n <= pages.Count; n++)
        {
            var page = pages[n - 1];
            Ensure(sizes[n - 1] <= OneMb + largest, $"Scan page {n} holds {sizes[n - 1]} bytes, more than 1 MB plus one item; sizes {Py.Repr(sizes)}");
            Ensure(
                page.ScannedCount == page.Count && page.Count == page.Items.Count,
                $"Scan page {n} Count {Py.Repr(page.Count)} ScannedCount {Py.Repr(page.ScannedCount)} for {page.Items.Count} items");
        }

        var got = pages.SelectMany(p => p.Items).Select(it => it["pk"].S).Order(StringComparer.Ordinal).ToList();
        Ensure(got.SequenceEqual(expected), $"paged Scan returned {got.Count} keys, expected each of {expected.Count} once: {Py.Repr(got)}");
        var filtered = await Walk(ddb, table, filterExpression: "attribute_exists(no_such_attr)");
        var summary = filtered.Select(p => (p.Count, p.ScannedCount, p.LastEvaluatedKey is not null)).ToList();
        Ensure(
            filtered.Count >= 2 && summary[0].Item3,
            $"a never-matching filter must not stop the 1 MB paging; pages (Count, ScannedCount, key) {Py.Repr(summary)}");
        Ensure(filtered.All(p => p.Count == 0 && p.Items.Count == 0), $"a never-matching filter returned items; pages {Py.Repr(summary)}");
        var scannedSum = filtered.Sum(p => p.ScannedCount ?? 0);
        Ensure(
            scannedSum == items.Count,
            $"filtered pages scanned {summary.Sum(s => s.ScannedCount ?? 0)} items, expected {items.Count}; pages {Py.Repr(summary)}");
    }
}

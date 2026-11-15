// Translated from behavioral/dynamodb/check_query_pages_at_1mb_before_a_never_matching_filter.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-pages-at-1mb-before-a-never-matching-filter</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryPagesAt1mbBeforeANeverMatchingFilter
{
    private const string Check = "query-pages-at-1mb-before-a-never-matching-filter";

    // ~50 KB per item -> ~20 items fit in a 1 MB result page, so 40 items must span >= 2 pages.
    private const int ItemBytesEach = 50 * 1024;
    private const int ItemCount = 40;
    private const int OneMb = 1024 * 1024;
    private const int MaxPages = 12;

    /// <summary>A cleanup running undo() where a failed delete raises.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };

    /// <summary>DynamoDB item size for an all-string item: UTF-8 length of each name plus its value.</summary>
    private static int ItemBytes(Dictionary<string, AttributeValue> item) =>
        item.Sum(e => Encoding.UTF8.GetByteCount(e.Key) + Encoding.UTF8.GetByteCount(e.Value.S));

    /// <summary>Every Query page over partition key pk with no Limit, following LastEvaluatedKey.</summary>
    private static async Task<List<QueryResponse>> Walk(IAmazonDynamoDB ddb, string table, string pk, string? filterExpression = null)
    {
        var pages = new List<QueryResponse>();
        var request = new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :p",
            ExpressionAttributeValues = Item((":p", S(pk))),
            ConsistentRead = true,
            FilterExpression = filterExpression,
        };
        while (true)
        {
            var resp = await ddb.QueryAsync(request);
            pages.Add(resp);
            if (resp.LastEvaluatedKey is null)
            {
                return pages;
            }

            Ensure(pages.Count < MaxPages, $"Query did not end after {MaxPages} pages");
            request.ExclusiveStartKey = resp.LastEvaluatedKey;
        }
    }

    /// <summary>
    /// Query truncates each page at the 1 MB limit before FilterExpression runs: over a partition
    /// of about 2 MB, a never-matching filter pages at the same two boundaries as the unfiltered
    /// Query, with Count 0 and the same ScannedCount and LastEvaluatedKey on each page.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-pages-at-1mb-before-a-never-matching-filter")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-ddb-qpage1mb-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(t), "ResourceNotFoundException"));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);
        var payload = new string('x', ItemBytesEach);
        var expectedSks = Enumerable.Range(0, ItemCount).Select(i => i.ToString("D3", CultureInfo.InvariantCulture)).ToList();
        foreach (var sk in expectedSks)
        {
            await ddb.PutItemAsync(t, Item(("pk", S("big")), ("sk", S(sk)), ("payload", S(payload))));
        }

        // Positive control: the unfiltered Query over the same partition truncates at 1 MB and
        // returns every item exactly once across its pages.
        var unfiltered = await Walk(ddb, t, "big");
        Ensure(unfiltered.Count == 2, $"a Query over ~{ItemCount * ItemBytesEach} bytes must span exactly 2 pages, got {unfiltered.Count}");
        for (var n = 1; n <= unfiltered.Count; n++)
        {
            var page = unfiltered[n - 1];
            Ensure(
                page.Count == page.ScannedCount && page.ScannedCount == page.Items.Count,
                $"unfiltered page {n}: Count {Py.Repr(page.Count)} ScannedCount {Py.Repr(page.ScannedCount)} for {page.Items.Count} items");
        }

        var firstBytes = unfiltered[0].Items.Sum(ItemBytes);
        Ensure(
            firstBytes > OneMb,
            $"unfiltered page 1 carries a LastEvaluatedKey, so it must have been truncated by the 1 MB budget and therefore exceed it; got {firstBytes} bytes");
        var seen = unfiltered.SelectMany(page => page.Items).Select(it => it["sk"].S).Order(StringComparer.Ordinal).ToList();
        Ensure(seen.SequenceEqual(expectedSks), $"unfiltered Query returned {seen.Count} keys, expected {expectedSks.Count}");

        // Main assertion: a never-matching FilterExpression pages at the SAME boundaries, proving the
        // 1 MB limit is applied before the filter rather than after it.
        var filtered = await Walk(ddb, t, "big", filterExpression: "attribute_exists(no_such_attr)");
        Ensure(
            filtered.Count == unfiltered.Count,
            $"a never-matching filter must not change the number of pages: unfiltered {unfiltered.Count}, filtered {filtered.Count}");
        for (var n = 1; n <= Math.Min(unfiltered.Count, filtered.Count); n++)
        {
            var u = unfiltered[n - 1];
            var f = filtered[n - 1];
            Ensure(f.Count == 0 && f.Items.Count == 0, $"filtered page {n} must carry no matches, got Count={f.Count} Items={Py.Repr(f.Items)}");
            Ensure(
                f.ScannedCount == u.ScannedCount && u.ScannedCount == u.Count,
                $"filtered page {n} ScannedCount {Py.Repr(f.ScannedCount)} must equal the unfiltered page's examined count {Py.Repr(u.ScannedCount)}");
            Ensure(
                (f.LastEvaluatedKey is not null) == (u.LastEvaluatedKey is not null),
                $"filtered page {n} LastEvaluatedKey presence {Py.Repr(f.LastEvaluatedKey is not null)} must match the unfiltered page's {Py.Repr(u.LastEvaluatedKey is not null)}");
            if (f.LastEvaluatedKey is not null)
            {
                Ensure(
                    Py.Equal(f.LastEvaluatedKey, u.LastEvaluatedKey),
                    $"filtered page {n} LastEvaluatedKey {Py.Repr(f.LastEvaluatedKey)} must equal the unfiltered page's {Py.Repr(u.LastEvaluatedKey)}");
            }
        }

        var scannedSum = filtered.Sum(p => p.ScannedCount ?? 0);
        Ensure(
            scannedSum == ItemCount,
            $"filtered pages must together scan every item exactly once ({ItemCount}); got {scannedSum}");
        Ensure(
            filtered[^1].LastEvaluatedKey is null,
            $"the last filtered page must reach the end of the partition with no LastEvaluatedKey, got {Py.Repr(filtered[^1].LastEvaluatedKey)}");
    }
}

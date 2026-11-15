// Translated from behavioral/dynamodb/check_query_natural_1mb_page_truncation.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-natural-1mb-page-truncation</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryNatural1mbPageTruncation
{
    private const string Check = "query-natural-1mb-page-truncation";

    // ~50 KB per item -> ~20 items fit in a 1 MB result page, so 40 items must span >= 2 pages.
    private const int ItemBytesEach = 50 * 1024;
    private const int ItemCount = 40;
    private const int OneMb = 1024 * 1024;
    private const int MaxPages = 12;

    // The 1 MB budget does not cap the page: a truncated page is the shortest prefix whose size
    // exceeds 1 MB (the item crossing the boundary is included), so `page <= 1 MB` would be wrong.

    /// <summary>DynamoDB item size for an all-string item: UTF-8 length of each name plus its value.</summary>
    private static int ItemBytes(Dictionary<string, AttributeValue> item) =>
        item.Sum(e => Encoding.UTF8.GetByteCount(e.Key) + Encoding.UTF8.GetByteCount(e.Value.S));

    /// <summary>The size real AWS charges a result page against its 1 MB budget.</summary>
    private static int PageBytes(IEnumerable<Dictionary<string, AttributeValue>> items) => items.Sum(ItemBytes);

    /// <summary>
    /// Assert a truncated page is the shortest prefix of the remaining results exceeding 1 MB.
    /// The page must exceed 1 MB, and dropping its last item must not.
    /// </summary>
    private static void AssertMinimalPrefix(QueryResponse resp, int pageNo)
    {
        var items = resp.Items;
        var total = PageBytes(items);
        var withoutLast = total - ItemBytes(items[^1]);
        Ensure(
            total > OneMb,
            $"page {pageNo} carries a LastEvaluatedKey, so it was truncated by the 1 MB result-set " +
            "budget and must therefore have reached it: real AWS includes the item that crosses the " +
            $"boundary, so the page exceeds 1 MB. Got {total} bytes over {items.Count} items, which is " +
            $"{OneMb - total} bytes short -- an implementation truncating before the budget is " +
            "reached hands the caller more pages than AWS would");
        Ensure(
            withoutLast <= OneMb,
            $"page {pageNo} must be the SHORTEST prefix exceeding 1 MB, but dropping its last item " +
            $"still leaves {withoutLast} bytes -- more than one item past the budget, so the page is " +
            "over-packed");
    }

    /// <summary>
    /// With no Limit, Query truncates at the 1 MB result-set limit: the page is the shortest prefix
    /// exceeding 1 MB, LastEvaluatedKey is its last item's key, and resuming yields the rest once each.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-natural-1mb-page-truncation")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);
        var payload = new string('x', ItemBytesEach);
        var expectedSks = Enumerable.Range(0, ItemCount).Select(i => i.ToString("D3", CultureInfo.InvariantCulture)).ToList();
        foreach (var sk in expectedSks)
        {
            await ddb.PutItemAsync(t, Item(("pk", S("big")), ("sk", S(sk)), ("payload", S(payload))));
        }

        QueryRequest Kwargs(Dictionary<string, AttributeValue>? exclusiveStartKey = null) => new()
        {
            TableName = t,
            KeyConditionExpression = "pk = :p",
            ExpressionAttributeValues = Item((":p", S("big"))),
            ConsistentRead = true,
            ExclusiveStartKey = exclusiveStartKey,
        };

        var first = await ddb.QueryAsync(Kwargs());
        Ensure(
            first.LastEvaluatedKey is not null,
            $"an unbounded Query over ~{ItemCount * ItemBytesEach} bytes must truncate at 1 MB and " +
            $"return a LastEvaluatedKey; got Count={first.Count} and no LastEvaluatedKey");
        Ensure(first.Count is > 0 and < ItemCount, $"first page must be a strict subset of the {ItemCount} items, got Count={first.Count}");
        Ensure(first.Count == first.Items.Count, $"Count {first.Count} != len(Items) {first.Items.Count}");
        Ensure(
            first.ScannedCount == first.Count,
            $"no FilterExpression, so ScannedCount must equal Count; got ScannedCount={first.ScannedCount} Count={first.Count}");
        AssertMinimalPrefix(first, 1);
        var lek = first.LastEvaluatedKey;
        Ensure(
            lek.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(["pk", "sk"]),
            $"a base-table Query LastEvaluatedKey carries exactly the table key attributes, got {Py.Repr(lek.Keys.Order(StringComparer.Ordinal).ToList())}");
        var lastItem = first.Items[^1];
        Ensure(
            Py.Equal(lek, Item(("pk", lastItem["pk"]), ("sk", lastItem["sk"]))),
            $"LastEvaluatedKey {Py.Repr(lek)} must be the key of the last item on the page " +
            $"({{\"pk\": {Py.Repr(lastItem["pk"])}, \"sk\": {Py.Repr(lastItem["sk"])}}})");
        var seen = first.Items.Select(it => it["sk"].S).ToList();
        var pages = 1;
        var resp = first;
        while (resp.LastEvaluatedKey is not null)
        {
            Ensure(pages < MaxPages, $"pagination did not terminate within {MaxPages} pages");
            resp = await ddb.QueryAsync(Kwargs(resp.LastEvaluatedKey));
            pages++;
            if (resp.LastEvaluatedKey is not null)
            {
                AssertMinimalPrefix(resp, pages);
            }
            else
            {
                // The terminal page is whatever is left, so it is bounded by the budget rather than
                // reaching it. Only the upper bound is meaningful here.
                var tailBytes = PageBytes(resp.Items);
                Ensure(
                    tailBytes <= OneMb + ItemBytes(resp.Items[^1]),
                    $"the terminal page must still respect the budget to within the single item that may cross it; page {pages} was {tailBytes} bytes");
            }

            seen.AddRange(resp.Items.Select(it => it["sk"].S));
        }

        Ensure(pages >= 2, $"expected at least 2 pages at 1 MB granularity, got {pages}");
        var distinct = seen.Distinct(StringComparer.Ordinal).Count();
        Ensure(seen.Count == distinct, $"pagination returned duplicates: {seen.Count} rows, {distinct} distinct");
        var divergence = seen.Count;
        for (var i = 0; i < Math.Min(seen.Count, expectedSks.Count); i++)
        {
            if (seen[i] != expectedSks[i])
            {
                divergence = i;
                break;
            }
        }

        Ensure(
            seen.SequenceEqual(expectedSks),
            $"paginated Query must return all {ItemCount} sort keys in order across {pages} pages; " +
            $"got {seen.Count} keys, first divergence at index {divergence}");
    }
}

// Translated from behavioral/dynamodb/check_lsi_consistent_query_pages_with_filter_counts_per_page.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.lsi-consistent-query-pages-with-filter-counts-per-page</summary>
[Category("AwsEvaluationSuite")]
public sealed class LsiConsistentQueryPagesWithFilterCountsPerPage
{
    private const string CheckId = "lsi-consistent-query-pages-with-filter-counts-per-page";
    private const string Index = "by-score";
    private const int Page = 4;
    private const int Items = 17;
    private static readonly string[] LekAttributes = ["pk", "sk", "score"];

    private sealed record PageResult(int Scanned, List<(string, string)> Items);

    private static string Repr(IEnumerable<PageResult> pages) =>
        Py.Repr(pages.Select(p => new Dictionary<string, object?> { ["scanned"] = p.Scanned, ["items"] = p.Items }).ToList());

    /// <summary>
    /// A strongly consistent LSI Query with Limit pages through LastEvaluatedKey (table and index keys)
    /// in index sort order; with a FilterExpression each page examines exactly Limit items
    /// (ScannedCount) and returns only matches (Count), so the pages sum to the partition and the filter.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.lsi-consistent-query-pages-with-filter-counts-per-page")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-lsi-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("score", ScalarAttributeType.N),
            ],
            LocalSecondaryIndexes =
            [
                new LocalSecondaryIndex
                {
                    IndexName = Index,
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("score", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => DeleteTableIfExists(ddb, table));
        await WaitActive(ddb, table);

        // Scores are negative, fractional and written out of order; the sort key order differs.
        var rows = new List<Dictionary<string, AttributeValue>>();
        for (var i = 0; i < Items; i++)
        {
            var whole = (i * 7 % Items) - 8;
            var score = i % 3 != 0
                ? $"{whole.ToString(CultureInfo.InvariantCulture)}.{(i % 3).ToString(CultureInfo.InvariantCulture)}"
                : whole.ToString(CultureInfo.InvariantCulture);
            var row = Item(
                ("pk", S("board")),
                ("sk", S("z" + (Items - i).ToString("00", CultureInfo.InvariantCulture))),
                ("score", N(score)),
                ("tier", S(i % 4 == 0 ? "gold" : "base")));
            await ddb.PutItemAsync(table, row);
            rows.Add(row);
        }

        await ddb.PutItemAsync(table, Item(("pk", S("other")), ("sk", S("a")), ("score", N("0")), ("tier", S("gold"))));
        var byScore = rows.OrderBy(r => double.Parse(r["score"].N, CultureInfo.InvariantCulture)).ToList();
        var wantAll = byScore.Select(r => (r["sk"].S, r["score"].N)).ToList();

        var pages = await Collect(ddb, table);
        var gotAll = pages.SelectMany(p => p.Items).ToList();
        Ensure(gotAll.SequenceEqual(wantAll), $"LSI Query collected {Py.Repr(gotAll)}, expected ascending score {Py.Repr(wantAll)}");
        Ensure(pages.All(p => p.Scanned == p.Items.Count), $"unfiltered pages: {Repr(pages)}");
        Ensure(
            pages.Select(p => p.Scanned).SequenceEqual(PageSizes(Items)),
            $"expected page sizes {Py.Repr(PageSizes(Items))} for {Items} items at Limit={Page}, got {Repr(pages)}");

        var fpages = await Collect(ddb, table, goldOnly: true);
        var wantGold = byScore.Where(r => r["tier"].S == "gold").Select(r => (r["sk"].S, r["score"].N)).ToList();
        Ensure(
            fpages.SelectMany(p => p.Items).SequenceEqual(wantGold),
            $"filtered LSI Query collected {Repr(fpages)}, expected only the gold rows {Py.Repr(wantGold)}");
        // Limit bounds the items examined before the filter, so page by page the matches are the gold
        // rows among exactly those Limit items.
        var sizes = PageSizes(Items);
        var starts = Enumerable.Range(0, sizes.Count).Select(n => sizes.Take(n).Sum()).ToList();
        var wantPages = starts.Zip(sizes, (s, n) => byScore.Skip(s).Take(n).Where(r => r["tier"].S == "gold").Select(r => (r["sk"].S, r["score"].N)).ToList()).ToList();
        var expectedPairs = sizes.Zip(wantPages, (n, w) => (n, w)).ToList();
        var gotPairs = fpages.Select(p => (p.Scanned, p.Items)).ToList();
        Ensure(
            gotPairs.Count == expectedPairs.Count && gotPairs.Zip(expectedPairs).All(z => z.First.Scanned == z.Second.n && z.First.Items.SequenceEqual(z.Second.w)),
            $"filtered pages: expected (ScannedCount, matches) {Py.Repr(expectedPairs)}, got {Repr(fpages)}");
    }

    private static List<int> PageSizes(int n) =>
        [.. Enumerable.Repeat(Page, n / Page), .. (n % Page != 0 ? new[] { n % Page } : [])];

    /// <summary>Every page of the board partition through the LSI, following LastEvaluatedKey.</summary>
    private static async Task<List<PageResult>> Collect(IAmazonDynamoDB ddb, string table, bool goldOnly = false)
    {
        var output = new List<PageResult>();
        Dictionary<string, AttributeValue>? start = null;
        while (true)
        {
            var request = new QueryRequest
            {
                TableName = table,
                IndexName = Index,
                Limit = Page,
                ConsistentRead = true,
                KeyConditionExpression = "pk = :p",
                ExpressionAttributeValues = Item((":p", S("board"))),
                ExclusiveStartKey = start,
            };
            if (goldOnly)
            {
                request.FilterExpression = "tier = :t";
                request.ExpressionAttributeValues[":t"] = S("gold");
            }

            var page = await ddb.QueryAsync(request);
            var items = page.Items;
            Ensure(page.Count == items.Count, $"page Count {Py.Repr(page.Count)} differs from its {items.Count} items");
            output.Add(new PageResult(page.ScannedCount ?? 0, [.. items.Select(i => (i["sk"].S, i["score"].N))]));
            Ensure(output.Count <= 20, "LastEvaluatedKey never ran out after 20 pages");
            start = page.LastEvaluatedKey;
            if (start is null || start.Count == 0)
            {
                // The final page may end exactly at the partition's end with a key or without one.
                return [.. output.Where(p => p.Scanned != 0)];
            }

            Ensure(
                start.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(LekAttributes),
                $"an LSI LastEvaluatedKey carries the table and index keys, got {Py.Repr(start.Keys.Order(StringComparer.Ordinal).ToList())}");
        }
    }

    private static async Task WaitActive(IAmazonDynamoDB ddb, string table, double timeoutSeconds = 40.0)
    {
        var clock = FidelityTime.StartNew();
        TableDescription? last = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            last = (await ddb.DescribeTableAsync(table)).Table;
            if (last.TableStatus == TableStatus.ACTIVE)
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(3));
        }

        throw Fail($"table {table} did not become ACTIVE: {Py.Repr(last)}");
    }

    private static async Task DeleteTableIfExists(IAmazonDynamoDB ddb, string table)
    {
        try
        {
            await ddb.DeleteTableAsync(table);
        }
        catch (ResourceNotFoundException)
        {
        }
    }
}

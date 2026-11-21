// Translated from behavioral/dynamodb/check_parallel_scan_totalsegments_4_segments_nest_exactly_inside_totalsegments_2_halves.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.parallel-scan-totalsegments-4-segments-nest-exactly-inside-totalsegments-2-halves</summary>
[Category("AwsEvaluationSuite")]
public sealed class ParallelScanTotalsegments4SegmentsNestExactlyInsideTotalsegments2Halves
{
    private const string Check = "parallel-scan-totalsegments-4-segments-nest-exactly-inside-totalsegments-2-halves";
    private const int Items = 256;
    private static readonly List<string> Written = [.. Enumerable.Range(0, Items).Select(i => "nest-" + i.ToString("0000", CultureInfo.InvariantCulture))];

    /// <summary>PutRequest every key in 25-item BatchWriteItem calls; raises on any unprocessed item.</summary>
    private static async Task BatchWrite(IAmazonDynamoDB ddb, string table, List<string> keys)
    {
        for (var offset = 0; offset < keys.Count; offset += 25)
        {
            var batch = keys.Skip(offset).Take(25);
            var resp = await ddb.BatchWriteItemAsync(new BatchWriteItemRequest
            {
                RequestItems = new Dictionary<string, List<WriteRequest>>(StringComparer.Ordinal)
                {
                    [table] = [.. batch.Select(k => new WriteRequest { PutRequest = new PutRequest { Item = Item(("pk", S(k))) } })],
                },
            });
            var unprocessed = resp.UnprocessedItems ?? [];
            Ensure(unprocessed.Count == 0, $"BatchWriteItem left {Py.Repr(unprocessed)} unprocessed while seeding");
        }
    }

    /// <summary>Every pk one segment of a (totalSegments, segment) parallel Scan returns, fully paged.</summary>
    private static async Task<HashSet<string>> ScanSegment(IAmazonDynamoDB ddb, string table, int totalSegments, int segment)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        Dictionary<string, AttributeValue>? start = null;
        while (true)
        {
            var page = await ddb.ScanAsync(new ScanRequest
            {
                TableName = table,
                TotalSegments = totalSegments,
                Segment = segment,
                ConsistentRead = true,
                ExclusiveStartKey = start,
            });
            keys.UnionWith(page.Items.Select(item => item["pk"].S));
            if (page.LastEvaluatedKey is null)
            {
                return keys;
            }

            start = page.LastEvaluatedKey;
        }
    }

    private static List<string> SymmetricDifference(HashSet<string> a, IEnumerable<string> b, int take)
    {
        var set = new HashSet<string>(a, StringComparer.Ordinal);
        set.SymmetricExceptWith(b);
        return [.. set.Order(StringComparer.Ordinal).Take(take)];
    }

    /// <summary>
    /// Doubling TotalSegments from 2 to 4 only ever splits a segment: each TotalSegments=4
    /// segment's items are a subset of exactly one TotalSegments=2 segment's items, and a
    /// TotalSegments=2 segment's items are exactly the union of its two TotalSegments=4 children.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Scan.html#Scan.ParallelScan
    /// </summary>
    [Test]
    [DisplayName("dynamodb.parallel-scan-totalsegments-4-segments-nest-exactly-inside-totalsegments-2-halves")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-nest-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        await BatchWrite(ddb, table, Written);

        var halves = new Dictionary<int, HashSet<string>>();
        foreach (var h in new[] { 0, 1 })
        {
            halves[h] = await ScanSegment(ddb, table, 2, h);
        }

        var quarters = new Dictionary<int, HashSet<string>>();
        foreach (var q in new[] { 0, 1, 2, 3 })
        {
            quarters[q] = await ScanSegment(ddb, table, 4, q);
        }

        var allFromHalves = new HashSet<string>(halves[0].Concat(halves[1]), StringComparer.Ordinal);
        var allFromQuarters = new HashSet<string>(quarters.Values.SelectMany(k => k), StringComparer.Ordinal);
        Ensure(
            allFromHalves.SetEquals(Written),
            $"TotalSegments=2 must cover every written key; symmetric difference {Py.Repr(SymmetricDifference(allFromHalves, Written, 20))}");
        Ensure(
            allFromQuarters.SetEquals(Written),
            $"TotalSegments=4 must cover every written key; symmetric difference {Py.Repr(SymmetricDifference(allFromQuarters, Written, 20))}");

        foreach (var q in new[] { 0, 1, 2, 3 })
        {
            Ensure(quarters[q].Count > 0, $"TotalSegments=4 segment {q} came back empty with {Items} items written");
        }

        // --- the nesting claim: segment q's parent is whichever half contains ALL of q's keys ---
        var parentOf = new Dictionary<int, int>();
        foreach (var (q, keys) in quarters)
        {
            var containing = new[] { 0, 1 }.Where(h => keys.IsSubsetOf(halves[h])).ToList();
            Ensure(
                containing.Count == 1,
                $"TotalSegments=4 segment {q} ({keys.Count} keys) must nest inside exactly one TotalSegments=2 half; it fits inside {Py.Repr(containing)} (half sizes {Py.Repr(new[] { halves[0].Count, halves[1].Count })})");
            parentOf[q] = containing[0];
        }

        foreach (var h in new[] { 0, 1 })
        {
            var children = parentOf.Where(p => p.Value == h).Select(p => p.Key).ToList();
            Ensure(
                children.Count == 2,
                $"TotalSegments=2 half {h} must have exactly 2 TotalSegments=4 children, got {Py.Repr(children)} (parent map {Py.Repr(parentOf)})");
            var union = new HashSet<string>(quarters[children[0]].Concat(quarters[children[1]]), StringComparer.Ordinal);
            Ensure(
                union.SetEquals(halves[h]),
                $"half {h}'s two children {Py.Repr(children)} must union to exactly half {h}'s {halves[h].Count} keys; symmetric difference {Py.Repr(SymmetricDifference(union, halves[h], 20))}");
            var overlap = quarters[children[0]].Intersect(quarters[children[1]], StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(10).ToList();
            Ensure(
                overlap.Count == 0,
                $"half {h}'s two children {Py.Repr(children)} must be disjoint; overlap {Py.Repr(overlap)}");
        }
    }
}

// Translated from behavioral/dynamodb/check_parallel_scan_segment_co_locates_every_sort_key_under_one_partition_key.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.parallel-scan-segment-co-locates-every-sort-key-under-one-partition-key</summary>
[Category("AwsEvaluationSuite")]
public sealed class ParallelScanSegmentCoLocatesEverySortKeyUnderOnePartitionKey
{
    private const string CheckId = "parallel-scan-segment-co-locates-every-sort-key-under-one-partition-key";
    private const int TotalSegments = 4;
    private static readonly string[] Pks = [.. Enumerable.Range(0, 16).Select(i => "pk" + i.ToString("000", CultureInfo.InvariantCulture))];
    private static readonly string[] Sks = ["sk-a", "sk-b", "sk-c", "sk-d"];

    /// <summary>PutRequest every item in 25-item BatchWriteItem calls; raises on any unprocessed item.</summary>
    private static async Task BatchWrite(IAmazonDynamoDB ddb, string table, List<WriteRequest> items)
    {
        for (var offset = 0; offset < items.Count; offset += 25)
        {
            var resp = await ddb.BatchWriteItemAsync(new BatchWriteItemRequest
            {
                RequestItems = new Dictionary<string, List<WriteRequest>>(StringComparer.Ordinal) { [table] = [.. items.Skip(offset).Take(25)] },
            });
            Ensure((resp.UnprocessedItems ?? []).Count == 0, $"BatchWriteItem left {Py.Repr(resp.UnprocessedItems)} unprocessed while seeding");
        }
    }

    /// <summary>Every item one segment of a TotalSegments=4 parallel Scan returns, fully paged.</summary>
    private static async Task<List<Dictionary<string, AttributeValue>>> ScanSegment(IAmazonDynamoDB ddb, string table, int segment)
    {
        var items = new List<Dictionary<string, AttributeValue>>();
        Dictionary<string, AttributeValue>? start = null;
        while (true)
        {
            var page = await ddb.ScanAsync(new ScanRequest
            {
                TableName = table,
                TotalSegments = TotalSegments,
                Segment = segment,
                ConsistentRead = true,
                ExclusiveStartKey = start,
            });
            items.AddRange(page.Items);
            if (page.LastEvaluatedKey is null)
            {
                return items;
            }

            start = page.LastEvaluatedKey;
        }
    }

    /// <summary>
    /// A parallel Scan assigns every item of one partition key to the same segment: a
    /// partition key's sort keys never split across two of the TotalSegments=4 segments.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Scan.html#Scan.ParallelScan
    /// </summary>
    [Test]
    [DisplayName("dynamodb.parallel-scan-segment-co-locates-every-sort-key-under-one-partition-key")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-segpk-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        var written = Pks.SelectMany(pk => Sks.Select(sk => new WriteRequest { PutRequest = new PutRequest { Item = Item(("pk", S(pk)), ("sk", S(sk))) } })).ToList();
        await BatchWrite(ddb, table, written);
        var allKeys = Pks.SelectMany(pk => Sks.Select(sk => (pk, sk))).ToHashSet();

        var perSegment = new Dictionary<int, List<Dictionary<string, AttributeValue>>>();
        for (var seg = 0; seg < TotalSegments; seg++)
        {
            perSegment[seg] = await ScanSegment(ddb, table, seg);
        }

        var flat = perSegment.Values.SelectMany(items => items.Select(it => (it["pk"].S, it["sk"].S))).ToList();
        static IOrderedEnumerable<(string, string)> Sorted(IEnumerable<(string, string)> keys) =>
            keys.OrderBy(k => k.Item1, StringComparer.Ordinal).ThenBy(k => k.Item2, StringComparer.Ordinal);
        Ensure(
            Sorted(flat).SequenceEqual(Sorted(allKeys)),
            $"the union of the {TotalSegments} segments must equal every written (pk, sk) exactly once; symmetric difference {Py.Repr(Sorted(flat.ToHashSet().Except(allKeys).Union(allKeys.Except(flat))).Take(20).ToList())}");

        var pkToSegments = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        foreach (var (seg, items) in perSegment)
        {
            foreach (var item in items)
            {
                if (!pkToSegments.TryGetValue(item["pk"].S, out var segments))
                {
                    pkToSegments[item["pk"].S] = segments = [];
                }

                segments.Add(seg);
            }
        }

        var split = pkToSegments.Where(p => p.Value.Count != 1).ToDictionary(p => p.Key, p => p.Value.Order().ToList(), StringComparer.Ordinal);
        Ensure(
            split.Count == 0,
            $"every sort key under one partition key must come back from exactly one Segment; {Py.Repr(split.Count)} partition key(s) split across segments: {Py.Repr(split)}");
        Ensure(
            pkToSegments.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(Pks),
            $"expected all {Py.Repr(Pks.Length)} partition keys to appear, got {Py.Repr(pkToSegments.Keys.Order(StringComparer.Ordinal).ToList())}");

        foreach (var pk in Pks)
        {
            var got = perSegment.Values.SelectMany(seg => seg).Where(it => it["pk"].S == pk).Select(it => it["sk"].S).ToHashSet(StringComparer.Ordinal);
            Ensure(
                got.SetEquals(Sks),
                $"partition key {Py.Repr(pk)} must return all {Py.Repr(Sks.Length)} of its sort keys from its single segment, got {Py.Repr(got.Order(StringComparer.Ordinal).ToList())}");
        }
    }
}

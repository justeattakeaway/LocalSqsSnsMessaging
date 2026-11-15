// Translated from behavioral/dynamodb/check_gsi_scan_pagination_over_tied_index_sort_key.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.gsi-scan-pagination-over-tied-index-sort-key</summary>
[Category("AwsEvaluationSuite")]
public sealed class GsiScanPaginationOverTiedIndexSortKey
{
    private const string Index = "gsi1";

    // Index sort-key values that tie four ways each; UTF-8 byte order is X < _x < x.
    private static readonly string[] Ranks = ["x", "X", "_x"];
    private static readonly string[] RankByteOrder = ["X", "_x", "x"];
    private static readonly string[] Partitions = ["p0", "p1", "p2", "p3"];
    private static readonly string[] SortKeys = ["s0", "s1", "s2"];
    private static readonly int[] Limits = [1, 2, 3, 5, 7, 10];
    private static readonly string[] LekAttributes = ["pk", "sk", "gk", "gs"];

    /// <summary>Poll DescribeTable every 2 s until the table and its indexes are ACTIVE.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var timeLeft = ctx.TimeLeftSeconds;
        var deadline = Math.Max(0.0, (timeLeft > 0 ? timeLeft : 300.0) - 30.0);
        var clock = FidelityTime.StartNew();
        TableDescription? desc = null;
        while (clock.Elapsed.TotalSeconds < deadline)
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

    /// <summary>Every item on the GSI via Scan, following LastEvaluatedKey; returns (items, page sizes, LEKs).</summary>
    private static async Task<(List<Dictionary<string, AttributeValue>> Items, List<int> PageSizes, List<Dictionary<string, AttributeValue>> Leks)> Scan(IAmazonDynamoDB ddb, string table, int? limit = null)
    {
        var request = new ScanRequest { TableName = table, IndexName = Index, Limit = limit };
        var items = new List<Dictionary<string, AttributeValue>>();
        var pageSizes = new List<int>();
        var leks = new List<Dictionary<string, AttributeValue>>();
        while (true)
        {
            var resp = await ddb.ScanAsync(request);
            items.AddRange(resp.Items);
            pageSizes.Add(resp.Items.Count);
            var lek = resp.LastEvaluatedKey;
            if (lek is null)
            {
                return (items, pageSizes, leks);
            }

            leks.Add(lek);
            Ensure(leks.Count <= 40, $"GSI scan paging did not end after 40 pages: {Py.Repr(leks)}");
            request.ExclusiveStartKey = lek;
        }
    }

    private static List<(string, string)> Keys(List<Dictionary<string, AttributeValue>> items) =>
        [.. items.Select(it => (it["pk"].S, it["sk"].S))];

    /// <summary>
    /// A Limit walk of a GSI Scan over a partition tied on the index sort key returns every item
    /// exactly once in the same order as an unpaged scan, and each LastEvaluatedKey names exactly
    /// the table key plus the GSI key.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/GSI.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.gsi-scan-pagination-over-tied-index-sort-key")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-gsiscantie-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("gk", ScalarAttributeType.S),
                new AttributeDefinition("gs", ScalarAttributeType.S),
            ],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = Index,
                    KeySchema = [new KeySchemaElement("gk", KeyType.HASH), new KeySchemaElement("gs", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await WaitActive(ddb, ctx, table);

        // Index partition G: 12 items, three distinct gs values, each shared by four items.
        var expected = new HashSet<(string, string)>();
        var n = 0;
        foreach (var p in Partitions)
        {
            foreach (var s in SortKeys)
            {
                await ddb.PutItemAsync(table, Item(("pk", S(p)), ("sk", S(s)), ("gk", S("G")), ("gs", S(Ranks[n % 3]))));
                expected.Add((p, s));
                n++;
            }
        }

        // A second, untied index partition proves cross-partition dedup still holds.
        await ddb.PutItemAsync(table, Item(("pk", S("p9")), ("sk", S("s0")), ("gk", S("H")), ("gs", S("x"))));
        expected.Add(("p9", "s0"));

        // Index reads are eventually consistent: poll until the whole table holds in the index.
        var timeLeft = ctx.TimeLeftSeconds;
        var deadline = Math.Max(0.0, (timeLeft > 0 ? timeLeft : 300.0) - 60.0);
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var seen = Keys((await Scan(ddb, table)).Items).ToHashSet();
            if (seen.SetEquals(expected))
            {
                break;
            }

            Ensure(clock.Elapsed.TotalSeconds < deadline, $"index never held all {expected.Count} items: {Py.Repr(seen)}");
            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        var (unpaged, _, _) = await Scan(ddb, table);
        var ukeys = Keys(unpaged);
        Ensure(
            ukeys.Count == expected.Count && ukeys.ToHashSet().SetEquals(expected),
            $"unpaged GSI scan returned {Py.Repr(ukeys)}, expected each of {Py.Repr(expected.OrderBy(k => k.Item1, StringComparer.Ordinal).ThenBy(k => k.Item2, StringComparer.Ordinal).ToList())} once");
        var again = Keys((await Scan(ddb, table)).Items);
        Ensure(again.SequenceEqual(ukeys), $"repeated unpaged GSI scan changed order: {Py.Repr(ukeys)} then {Py.Repr(again)}");

        var gRanks = unpaged.Where(it => it["gk"].S == "G").Select(it => it["gs"].S).ToList();
        var wantRanks = RankByteOrder.SelectMany(r => Enumerable.Repeat(r, 4)).ToList();
        Ensure(gRanks.SequenceEqual(wantRanks), $"index partition G's sort-key order {Py.Repr(gRanks)}, expected byte order {Py.Repr(wantRanks)}");

        foreach (var limit in Limits)
        {
            var (items, pageSizes, leks) = await Scan(ddb, table, limit);
            var walk = Keys(items);
            Ensure(walk.SequenceEqual(ukeys), $"Limit={limit} walk {Py.Repr(walk)} != unpaged {Py.Repr(ukeys)}");
            Ensure(pageSizes.Max() <= limit && leks.Count >= 1, $"Limit={limit} page sizes {Py.Repr(pageSizes)} did not page as expected");
            foreach (var lek in leks)
            {
                Ensure(lek.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(LekAttributes), $"LastEvaluatedKey {Py.Repr(lek)} must name exactly pk, sk, gk and gs");
            }
        }
    }
}

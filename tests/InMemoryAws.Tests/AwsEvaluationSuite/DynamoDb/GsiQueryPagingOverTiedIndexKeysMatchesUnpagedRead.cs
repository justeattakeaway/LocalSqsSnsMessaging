// Translated from behavioral/dynamodb/check_gsi_query_paging_over_tied_index_keys_matches_unpaged_read.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.gsi-query-paging-over-tied-index-keys-matches-unpaged-read</summary>
[Category("AwsEvaluationSuite")]
public sealed class GsiQueryPagingOverTiedIndexKeysMatchesUnpagedRead
{
    private const string HashOnly = "by-group";
    private const string WithRange = "by-group-rank";

    // Index sort-key values that tie four ways each; UTF-8 byte order is X < _x < x.
    private static readonly string[] Ranks = ["x", "X", "_x"];
    private static readonly string[] RankByteOrder = ["X", "_x", "x"];
    private static readonly int[] Limits = [1, 2, 3, 5];
    private static readonly string[] Partitions = ["p0", "p1", "p2", "p3"];
    private static readonly string[] SortKeys = ["s0", "s1", "s2"];
    private static readonly string[] Indexes = [HashOnly, WithRange];

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

    /// <summary>Every item of partition G on the index, following LastEvaluatedKey; returns (items, pages).</summary>
    private static async Task<(List<Dictionary<string, AttributeValue>> Items, List<int> Pages)> Read(IAmazonDynamoDB ddb, string table, string index, bool forward, int? limit = null)
    {
        var request = new QueryRequest
        {
            TableName = table,
            IndexName = index,
            KeyConditionExpression = "gk = :g",
            ExpressionAttributeValues = Item((":g", S("G"))),
            ScanIndexForward = forward,
            Limit = limit,
        };
        var items = new List<Dictionary<string, AttributeValue>>();
        var pages = new List<int>();
        while (true)
        {
            var resp = await ddb.QueryAsync(request);
            pages.Add(resp.Items.Count);
            items.AddRange(resp.Items);
            if (resp.LastEvaluatedKey is null)
            {
                return (items, pages);
            }

            Ensure(pages.Count <= 40, $"{index} paging did not end after 40 pages: {Py.Repr(pages)}");
            request.ExclusiveStartKey = resp.LastEvaluatedKey;
        }
    }

    private static List<(string, string)> Keys(List<Dictionary<string, AttributeValue>> items) =>
        [.. items.Select(it => (it["pk"].S, it["sk"].S))];

    /// <summary>
    /// GSI Query over items tying on the index key returns each item once, in byte order of the sort
    /// key; a Limit 1/2/3/5 walk equals the unpaged read, and ScanIndexForward=False is its mirror.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/GSI.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.gsi-query-paging-over-tied-index-keys-matches-unpaged-read")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-gsitie-");
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
                    IndexName = HashOnly,
                    KeySchema = [new KeySchemaElement("gk", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
                new GlobalSecondaryIndex
                {
                    IndexName = WithRange,
                    KeySchema = [new KeySchemaElement("gk", KeyType.HASH), new KeySchemaElement("gs", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await WaitActive(ddb, ctx, table);

        // 12 items in index partition G: base sort keys repeat across partitions, ranks tie 4 ways.
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

        await ddb.PutItemAsync(table, Item(("pk", S("p9")), ("sk", S("s0")), ("gk", S("H")), ("gs", S("x"))));

        // Index reads are eventually consistent: poll until both indexes hold every item.
        var timeLeft = ctx.TimeLeftSeconds;
        var deadline = Math.Max(0.0, (timeLeft > 0 ? timeLeft : 300.0) - 60.0);
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var seen = new Dictionary<string, List<(string, string)>>(StringComparer.Ordinal);
            foreach (var idx in Indexes)
            {
                seen[idx] = Keys((await Read(ddb, table, idx, true)).Items);
            }

            if (seen.Values.All(v => v.ToHashSet().SetEquals(expected)))
            {
                break;
            }

            Ensure(clock.Elapsed.TotalSeconds < deadline, $"indexes never held all {expected.Count} items: {Py.Repr(seen)}");
            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        foreach (var index in Indexes)
        {
            var (forward, _) = await Read(ddb, table, index, true);
            var fkeys = Keys(forward);
            Ensure(
                fkeys.Count == expected.Count && fkeys.ToHashSet().SetEquals(expected),
                $"{index} unpaged forward read returned {Py.Repr(fkeys)}, expected each of {Py.Repr(expected.OrderBy(k => k.Item1, StringComparer.Ordinal).ThenBy(k => k.Item2, StringComparer.Ordinal).ToList())} once");
            var again = Keys((await Read(ddb, table, index, true)).Items);
            Ensure(again.SequenceEqual(fkeys), $"{index} repeated forward read changed order: {Py.Repr(fkeys)} then {Py.Repr(again)}");
            var backward = Keys((await Read(ddb, table, index, false)).Items);
            Ensure(
                backward.SequenceEqual(Enumerable.Reverse(fkeys)),
                $"{index} ScanIndexForward=False {Py.Repr(backward)} is not the mirror of forward {Py.Repr(fkeys)}");
            if (index == WithRange)
            {
                var ranks = forward.Select(it => it["gs"].S).ToList();
                var want = RankByteOrder.SelectMany(r => Enumerable.Repeat(r, 4)).ToList();
                Ensure(ranks.SequenceEqual(want), $"{index} sort-key order {Py.Repr(ranks)}, expected byte order {Py.Repr(want)}");
            }

            foreach (var limit in Limits)
            {
                foreach (var (direction, unpaged) in new[] { (true, fkeys), (false, backward) })
                {
                    var (items, pages) = await Read(ddb, table, index, direction, limit);
                    var walk = Keys(items);
                    Ensure(
                        walk.SequenceEqual(unpaged),
                        $"{index} Limit={limit} forward={(direction ? "True" : "False")} walk {Py.Repr(walk)} != unpaged {Py.Repr(unpaged)}");
                    Ensure(
                        pages.Max() <= limit && pages.Count >= 2 && pages[1] > 0,
                        $"{index} Limit={limit} forward={(direction ? "True" : "False")} page sizes {Py.Repr(pages)}");
                }
            }
        }
    }
}

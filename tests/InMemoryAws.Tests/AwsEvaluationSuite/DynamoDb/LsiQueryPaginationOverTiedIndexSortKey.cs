// Translated from behavioral/dynamodb/check_lsi_query_pagination_over_tied_index_sort_key.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.lsi-query-pagination-over-tied-index-sort-key</summary>
[Category("AwsEvaluationSuite")]
public sealed class LsiQueryPaginationOverTiedIndexSortKey
{
    private const string Index = "lsi1";
    private static readonly int[] Ranks = [1, 2, 3];
    private static readonly int[] Limits = [1, 2, 3, 5, 7];
    private static readonly string[] LekAttributes = ["pk", "sk", "lsk"];

    /// <summary>Every item of partition p0 on the LSI, following LastEvaluatedKey; returns (items, page sizes, LEKs).</summary>
    private static async Task<(List<Dictionary<string, AttributeValue>> Items, List<int> PageSizes, List<Dictionary<string, AttributeValue>> Leks)> Read(IAmazonDynamoDB ddb, string table, bool forward, int? limit = null)
    {
        var request = new QueryRequest
        {
            TableName = table,
            IndexName = Index,
            KeyConditionExpression = "pk = :p",
            ExpressionAttributeValues = Item((":p", S("p0"))),
            ScanIndexForward = forward,
            ConsistentRead = true,
            Limit = limit,
        };
        var items = new List<Dictionary<string, AttributeValue>>();
        var pageSizes = new List<int>();
        var leks = new List<Dictionary<string, AttributeValue>>();
        while (true)
        {
            var resp = await ddb.QueryAsync(request);
            items.AddRange(resp.Items);
            pageSizes.Add(resp.Items.Count);
            var lek = resp.LastEvaluatedKey;
            if (lek is null)
            {
                return (items, pageSizes, leks);
            }

            leks.Add(lek);
            Ensure(leks.Count <= 40, $"LSI query paging did not end after 40 pages: {Py.Repr(leks)}");
            request.ExclusiveStartKey = lek;
        }
    }

    private static List<(string, string)> Keys(List<Dictionary<string, AttributeValue>> items) =>
        [.. items.Select(it => (it["pk"].S, it["sk"].S))];

    /// <summary>
    /// A Limit walk of an LSI Query over items tied on the index sort key returns every item
    /// exactly once in the same order as an unpaged read, and each LastEvaluatedKey names exactly
    /// the table key plus the LSI sort key.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/LSI.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.lsi-query-pagination-over-tied-index-sort-key")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-lsitie-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("lsk", ScalarAttributeType.N),
            ],
            LocalSecondaryIndexes =
            [
                new LocalSecondaryIndex
                {
                    IndexName = Index,
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("lsk", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        // 12 items in partition p0: three distinct lsk values, each shared by four items.
        var expected = new HashSet<(string, string)>();
        for (var i = 0; i < 12; i++)
        {
            var sk = "s" + i.ToString("00", CultureInfo.InvariantCulture);
            await ddb.PutItemAsync(table, Item(("pk", S("p0")), ("sk", S(sk)), ("lsk", N(Ranks[i % 3].ToString(CultureInfo.InvariantCulture)))));
            expected.Add(("p0", sk));
        }

        // A second partition proves the tie-break stays scoped to p0.
        await ddb.PutItemAsync(table, Item(("pk", S("p1")), ("sk", S("s00")), ("lsk", N("1"))));

        var (forward, _, _) = await Read(ddb, table, true);
        var fkeys = Keys(forward);
        Ensure(
            fkeys.Count == expected.Count && fkeys.ToHashSet().SetEquals(expected),
            $"unpaged LSI query returned {Py.Repr(fkeys)}, expected each of {Py.Repr(expected.OrderBy(k => k.Item1, StringComparer.Ordinal).ThenBy(k => k.Item2, StringComparer.Ordinal).ToList())} once");
        var rankSeq = forward.Select(it => int.Parse(it["lsk"].N, CultureInfo.InvariantCulture)).ToList();
        var wantRanks = Ranks.Order().SelectMany(r => Enumerable.Repeat(r, 4)).ToList();
        Ensure(rankSeq.SequenceEqual(wantRanks), $"LSI sort-key order {Py.Repr(rankSeq)}, expected grouped order {Py.Repr(wantRanks)}");

        var backward = Keys((await Read(ddb, table, false)).Items);
        Ensure(backward.SequenceEqual(Enumerable.Reverse(fkeys)), $"ScanIndexForward=False {Py.Repr(backward)} is not the mirror of forward {Py.Repr(fkeys)}");

        foreach (var limit in Limits)
        {
            foreach (var (direction, unpaged) in new[] { (true, fkeys), (false, backward) })
            {
                var (items, pageSizes, leks) = await Read(ddb, table, direction, limit);
                var walk = Keys(items);
                Ensure(walk.SequenceEqual(unpaged), $"Limit={limit} forward={(direction ? "True" : "False")} walk {Py.Repr(walk)} != unpaged {Py.Repr(unpaged)}");
                Ensure(
                    pageSizes.Max() <= limit && leks.Count >= 1,
                    $"Limit={limit} forward={(direction ? "True" : "False")} page sizes {Py.Repr(pageSizes)} did not page as expected");
                foreach (var lek in leks)
                {
                    Ensure(lek.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(LekAttributes), $"LastEvaluatedKey {Py.Repr(lek)} must name exactly pk, sk and lsk");
                }
            }
        }
    }
}

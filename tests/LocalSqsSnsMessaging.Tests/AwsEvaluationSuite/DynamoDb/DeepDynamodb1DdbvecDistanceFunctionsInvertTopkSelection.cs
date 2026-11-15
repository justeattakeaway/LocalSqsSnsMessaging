// Translated from behavioral/dynamodb/check_deep_dynamodb_1_ddbvec_distance_functions_invert_topk_selection.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.deep-dynamodb-1-ddbvec-distance-functions-invert-topk-selection</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeepDynamodb1DdbvecDistanceFunctionsInvertTopkSelection
{
    private const string CheckId = "deep-dynamodb-1-ddbvec-distance-functions-invert-topk-selection";

    // One 4-d attribute, five items chosen so the three metrics disagree on WHICH three items are
    // the top-3, not merely on their order. Query q = [1, 0, 0, 0].
    //   item   vector          cosine dist   euclidean     dot product
    //   a      [1, 0, 0, 0]    0             0             1
    //   b      [4, 1, 0, 0]    0.02986       3.16228       4
    //   c      [0.5,0.5,0,0]   0.29289       0.70711       0.5
    //   e      [0, 0, 1, 0]    1             1.41421       0
    //   f      [-2, 0, 0, 0]   2             3             -2
    private static readonly (string Pk, double[] Vector)[] Items =
    [
        ("a", [1.0, 0.0, 0.0, 0.0]),
        ("b", [4.0, 1.0, 0.0, 0.0]),
        ("c", [0.5, 0.5, 0.0, 0.0]),
        ("e", [0.0, 0.0, 1.0, 0.0]),
        ("f", [-2.0, 0.0, 0.0, 0.0]),
    ];

    private static readonly double[] Query = [1.0, 0.0, 0.0, 0.0];

    private static double[] ItemVector(string pk) => Items.First(i => i.Pk == pk).Vector;

    private static double CosDist(double[] u, double[] v)
    {
        var dot = u.Zip(v).Sum(p => p.First * p.Second);
        return 1.0 - (dot / (Math.Sqrt(u.Sum(x => x * x)) * Math.Sqrt(v.Sum(y => y * y))));
    }

    private static double Euclid(double[] u, double[] v) => Math.Sqrt(u.Zip(v).Sum(p => Math.Pow(p.First - p.Second, 2)));

    private static double Dot(double[] u, double[] v) => u.Zip(v).Sum(p => p.First * p.Second);

    /// <summary>
    /// SearchVectors TopK takes k smallest scores for COSINE/EUCLIDEAN, k highest for DOT_PRODUCT.
    /// Three indexes over one attribute must select three different top-3 sets for the same query.
    /// </summary>
    [Test]
    [KnownFidelityGap(FidelityGap.DynamoDbVectorSearch, "CreateTable accepts VectorIndexes but drops them: DescribeTable reports none")]
    [DisplayName("dynamodb.deep-dynamodb-1-ddbvec-distance-functions-invert-topk-selection")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbvec-");

        static VectorIndex Vi(string name, string fn) => new()
        {
            IndexName = name,
            VectorAttribute = new VectorAttributeDefinition { AttributeName = "emb" },
            Dimensions = 4,
            DistanceFunction = fn,
            Projection = new Projection { ProjectionType = ProjectionType.ALL },
        };

        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            VectorIndexes = [Vi("idx-cos", "COSINE"), Vi("idx-euc", "EUCLIDEAN"), Vi("idx-dot", "DOT_PRODUCT")],
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        var described = await WaitVectorTableActive(ddb, table);
        var byName = (described.VectorIndexes ?? []).ToDictionary(v => v.IndexName, StringComparer.Ordinal);
        var functions = byName.ToDictionary(e => e.Key, e => e.Value.DistanceFunction?.ToString(), StringComparer.Ordinal);
        Ensure(
            Py.Equal(functions, new Dictionary<string, string?> { ["idx-cos"] = "COSINE", ["idx-euc"] = "EUCLIDEAN", ["idx-dot"] = "DOT_PRODUCT" }),
            $"DescribeTable vector indexes were {Py.Repr(functions.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => (e.Key, e.Value)).ToList())}");
        foreach (var (pk, vec) in Items)
        {
            await ddb.PutItemAsync(table, Item(("pk", S(pk)), ("emb", new AttributeValue { L = Vec(vec) })));
        }

        var expected = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["idx-cos"] = Items.Select(i => i.Pk).OrderBy(k => CosDist(Query, ItemVector(k))).Take(3).ToList(),
            ["idx-euc"] = Items.Select(i => i.Pk).OrderBy(k => Euclid(Query, ItemVector(k))).Take(3).ToList(),
            ["idx-dot"] = Items.Select(i => i.Pk).OrderBy(k => -Dot(Query, ItemVector(k))).Take(3).ToList(),
        };

        // The design premise, pinned so a future edit to Items cannot silently collapse it.
        Ensure(
            Py.Equal(expected, new Dictionary<string, List<string>>
            {
                ["idx-cos"] = ["a", "b", "c"],
                ["idx-euc"] = ["a", "c", "e"],
                ["idx-dot"] = ["b", "a", "c"],
            }),
            $"fixture premise broke: {Py.Repr(expected)}");

        // Writes become searchable asynchronously; poll the full population (TopK=5) on every index
        // until all five items are visible before grading the TopK=3 selection.
        foreach (var idx in expected.Keys)
        {
            var full = await SearchUntil(ddb, table, idx, 5, res => res.Count == 5);
            Ensure(full.Count == 5, $"{idx}: only {full.Count} of 5 written items became searchable");
        }

        var metric = new Dictionary<string, Func<double[], double[], double>>(StringComparer.Ordinal)
        {
            ["idx-cos"] = CosDist,
            ["idx-euc"] = Euclid,
            ["idx-dot"] = Dot,
        };
        var got = new Dictionary<string, List<(string Pk, double Score)>>(StringComparer.Ordinal);
        foreach (var (idx, want) in expected)
        {
            var res = (await ddb.SearchVectorsAsync(new SearchVectorsRequest
            {
                TableName = table,
                IndexName = idx,
                TopK = 3,
                SearchVector = Vec(Query),
            })).SearchResults ?? [];
            var scored = res.Select(r => (Pk: r.Item["pk"].S, Score: (double)r.Score!)).ToList();
            got[idx] = scored;
            Ensure(
                scored.Select(s => s.Pk).SequenceEqual(want),
                $"{idx}: TopK=3 selected {Py.Repr(scored)}, expected the items {Py.Repr(want)}");
            foreach (var (pk, score) in scored)
            {
                var reference = metric[idx](Query, ItemVector(pk));
                Ensure(
                    Math.Abs(score - reference) < 1e-3,
                    $"{idx}: item {Py.Repr(pk)} scored {Py.Repr(score)}, the metric gives {Py.Repr(reference)}");
            }
        }

        var cosScores = got["idx-cos"].Select(s => s.Score).ToList();
        var eucScores = got["idx-euc"].Select(s => s.Score).ToList();
        var dotScores = got["idx-dot"].Select(s => s.Score).ToList();
        Ensure(cosScores.SequenceEqual(cosScores.Order()), $"COSINE scores not ascending: {Py.Repr(cosScores)}");
        Ensure(eucScores.SequenceEqual(eucScores.Order()), $"EUCLIDEAN scores not ascending: {Py.Repr(eucScores)}");
        Ensure(
            dotScores.SequenceEqual(dotScores.OrderDescending()),
            $"DOT_PRODUCT scores not descending: {Py.Repr(dotScores)}");

        // The inversion itself: item b is in the dot-product top-3 at rank 1 and excluded from the
        // euclidean top-3, because it is the farthest point yet has the largest dot product.
        Ensure(
            got["idx-dot"][0].Pk == "b" && !got["idx-euc"].Any(s => s.Pk == "b"),
            $"dot top-3 {Py.Repr(got["idx-dot"])} vs euclidean top-3 {Py.Repr(got["idx-euc"])}: "
            + "DOT_PRODUCT must take the k HIGHEST scores and EUCLIDEAN the k SMALLEST");
    }

    /// <summary><c>[{'N': repr(float(x))} for x in values]</c>.</summary>
    private static List<AttributeValue> Vec(IEnumerable<double> values) => values.Select(x => N(PyFloat(x))).ToList();

    private static string PyFloat(double x)
    {
        var text = x.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.', StringComparison.Ordinal) || text.Contains('E', StringComparison.Ordinal) ? text : text + ".0";
    }

    private static async Task<TableDescription> WaitVectorTableActive(IAmazonDynamoDB ddb, string table, int timeoutSeconds = 240)
    {
        var clock = FidelityTime.StartNew();
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        var tbl = (await ddb.DescribeTableAsync(table)).Table;
        Ensure(
            tbl.VectorIndexes is { Count: > 0 },
            "CreateTable accepted VectorIndexes but DescribeTable reports none - the vector indexes "
            + "were silently dropped");
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            tbl = (await ddb.DescribeTableAsync(table)).Table;
            if (tbl.TableStatus == TableStatus.ACTIVE && (tbl.VectorIndexes ?? []).All(v => v.IndexStatus?.ToString() == "ACTIVE"))
            {
                return tbl;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail(
            $"table/vector indexes never became ACTIVE: {Py.Repr(tbl.TableStatus?.Value)} "
            + $"{Py.Repr((tbl.VectorIndexes ?? []).Select(v => (v.IndexName, v.IndexStatus?.ToString())).ToList())}");
    }

    private static async Task<List<SearchResultItem>> SearchUntil(
        IAmazonDynamoDB ddb, string table, string index, int topK, Func<List<SearchResultItem>, bool> done, int timeoutSeconds = 90)
    {
        var clock = FidelityTime.StartNew();
        List<SearchResultItem> res = [];
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            res = (await ddb.SearchVectorsAsync(new SearchVectorsRequest
            {
                TableName = table,
                IndexName = index,
                TopK = topK,
                SearchVector = Vec(Query),
            })).SearchResults ?? [];
            if (done(res))
            {
                return res;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
        }

        return res;
    }
}

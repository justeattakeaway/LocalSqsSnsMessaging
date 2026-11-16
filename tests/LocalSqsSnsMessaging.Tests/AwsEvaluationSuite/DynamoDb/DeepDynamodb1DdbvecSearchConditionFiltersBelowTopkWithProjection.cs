// Translated from behavioral/dynamodb/check_deep_dynamodb_1_ddbvec_search_condition_filters_below_topk_with_projection.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.deep-dynamodb-1-ddbvec-search-condition-filters-below-topk-with-projection</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeepDynamodb1DdbvecSearchConditionFiltersBelowTopkWithProjection
{
    private const string CheckId = "deep-dynamodb-1-ddbvec-search-condition-filters-below-topk-with-projection";

    // Six items, two of them in category "x". TopK is 10, larger than BOTH the matching subset (2)
    // and the whole table (6), so the only thing that can shrink the result is the filter.
    private static readonly (string Pk, double[] Vector, string Cat)[] Items =
    [
        ("a", [1.0, 0.0, 0.0, 0.0], "x"),
        ("b", [4.0, 1.0, 0.0, 0.0], "y"),
        ("c", [0.5, 0.5, 0.0, 0.0], "x"),
        ("e", [0.0, 0.0, 1.0, 0.0], "y"),
        ("f", [-2.0, 0.0, 0.0, 0.0], "y"),
        ("g", [1.0, 0.1, 0.0, 0.0], "z"),
    ];

    private static readonly double[] Query = [1.0, 0.0, 0.0, 0.0];

    private static double Euclid(double[] u, double[] v) => Math.Sqrt(u.Zip(v).Sum(p => Math.Pow(p.First - p.Second, 2)));

    private static double[] ItemVector(string pk) => Items.First(i => i.Pk == pk).Vector;

    /// <summary>
    /// An INLINE_FILTER condition returns only matching items nearest-first, fewer than TopK.
    /// ProjectionExpression trims each result to exactly the named attributes.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.deep-dynamodb-1-ddbvec-search-condition-filters-below-topk-with-projection")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbvec-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],

            // A SearchSchema attribute must be declared in AttributeDefinitions (CreateTable otherwise
            // fails "One element in SearchSchema is not defined in attribute definitions").
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("cat", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            VectorIndexes =
            [
                new VectorIndex
                {
                    IndexName = "idx-euc",
                    VectorAttribute = new VectorAttributeDefinition { AttributeName = "emb" },
                    Dimensions = 4,
                    DistanceFunction = "EUCLIDEAN",
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                    SearchSchema = [new SearchSchemaElement { AttributeName = "cat", SearchSchemaElementType = SearchSchemaElementType.INLINE_FILTER }],
                },
            ],
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        var described = await WaitVectorTableActive(ddb, table);
        var schema = described.VectorIndexes[0].SearchSchema?.Select(e => new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["AttributeName"] = e.AttributeName,
            ["SearchSchemaElementType"] = e.SearchSchemaElementType?.ToString(),
        }).ToList();
        Ensure(
            Py.Equal(schema, new List<Dictionary<string, string?>> { new() { ["AttributeName"] = "cat", ["SearchSchemaElementType"] = "INLINE_FILTER" } }),
            $"the vector index search schema was described back as {Py.Repr(schema)}");
        foreach (var (pk, vec, cat) in Items)
        {
            await ddb.PutItemAsync(table, Item(
                ("pk", S(pk)),
                ("emb", new AttributeValue { L = Vec(vec) }),
                ("cat", S(cat)),
                ("note", S($"note-{pk}")),
                ("extra", N("7"))));
        }

        // Positive control: unfiltered and unprojected, every item comes back with every attribute.
        var control = await SearchUntil(ddb, table, 10, res => res.Count == Items.Length);
        var allPks = Items.Select(i => i.Pk).Order(StringComparer.Ordinal).ToList();
        Ensure(
            control.Select(r => r.Item["pk"].S).Order(StringComparer.Ordinal).SequenceEqual(allPks),
            $"unfiltered TopK=10 returned {Py.Repr(control.Select(r => r.Item["pk"].S).ToList())}, "
            + $"expected all of {Py.Repr(allPks)}");

        // ProjectionType ALL returns every non-vector attribute. The vector attribute itself is NOT
        // returned by SearchVectors.
        foreach (var r in control)
        {
            Ensure(
                r.Item.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(["pk", "cat", "note", "extra"]),
                $"unprojected search item carried {Py.Repr(r.Item.Keys.Order(StringComparer.Ordinal).ToList())}, expected pk, cat, note, extra");
        }

        var want = Items.Where(i => i.Cat == "x").Select(i => i.Pk).OrderBy(pk => Euclid(Query, ItemVector(pk))).ToList();
        Ensure(want.SequenceEqual(["a", "c"]), $"fixture premise broke: {Py.Repr(want)}");
        var res = (await ddb.SearchVectorsAsync(new SearchVectorsRequest
        {
            TableName = table,
            IndexName = "idx-euc",
            TopK = 10,
            SearchVector = Vec(Query),
            SearchConditionExpression = "#c = :x",
            ExpressionAttributeNames = Names(("#c", "cat")),
            ExpressionAttributeValues = Item((":x", S("x"))),
            ProjectionExpression = "pk, note",
        })).SearchResults ?? [];
        var got = res.Select(r => r.Item.GetValueOrDefault("pk")?.S).ToList();
        Ensure(res.Count < 10, $"the filter did not shrink the result: {res.Count} items for TopK=10");
        Ensure(got.SequenceEqual(want), $"filtered search returned {Py.Repr(got)}, expected exactly {Py.Repr(want)} nearest-first");
        foreach (var r in res)
        {
            Ensure(
                r.Item.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(["pk", "note"]),
                $"ProjectionExpression \"pk, note\" returned attributes {Py.Repr(r.Item.Keys.Order(StringComparer.Ordinal).ToList())}");
            var pk = r.Item["pk"].S;
            Ensure(Py.Equal(r.Item["note"], S($"note-{pk}")), $"projected note was {Py.Repr(r.Item["note"])}");
            Ensure(
                Math.Abs((double)r.Score! - Euclid(Query, ItemVector(pk))) < 1e-3,
                $"item {Py.Repr(pk)} scored {Py.Repr(r.Score)} under the filter");
            var stored = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;
            Ensure(
                Py.Equal(stored["cat"], S("x")),
                $"filtered search returned {Py.Repr(pk)}, whose stored cat is {Py.Repr(stored["cat"])}");
        }
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
            "CreateTable accepted VectorIndexes but DescribeTable reports none - the vector index "
            + "was silently dropped");
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
            $"table/vector index never became ACTIVE: {Py.Repr(tbl.TableStatus?.Value)} "
            + $"{Py.Repr((tbl.VectorIndexes ?? []).Select(v => (v.IndexName, v.IndexStatus?.ToString())).ToList())}");
    }

    private static async Task<List<SearchResultItem>> SearchUntil(
        IAmazonDynamoDB ddb, string table, int topK, Func<List<SearchResultItem>, bool> done, int timeoutSeconds = 90)
    {
        var clock = FidelityTime.StartNew();
        List<SearchResultItem> res = [];
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            res = (await ddb.SearchVectorsAsync(new SearchVectorsRequest
            {
                TableName = table,
                IndexName = "idx-euc",
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

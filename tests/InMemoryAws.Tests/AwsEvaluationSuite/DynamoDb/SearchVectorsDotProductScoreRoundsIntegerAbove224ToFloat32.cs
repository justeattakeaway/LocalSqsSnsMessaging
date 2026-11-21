// Translated from behavioral/dynamodb/check_search_vectors_dot_product_score_rounds_integer_above_2_24_to_float32.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.search-vectors-dot-product-score-rounds-integer-above-2-24-to-float32</summary>
[Category("AwsEvaluationSuite")]
public sealed class SearchVectorsDotProductScoreRoundsIntegerAbove224ToFloat32
{
    private const string CheckId = "search-vectors-dot-product-score-rounds-integer-above-2-24-to-float32";

    // 2**24 is the largest integer every float32 represents exactly; 2**24 + 1 is the smallest
    // positive integer float32 cannot, and rounds down to 2**24.
    private const string Unrounded = "16777216";
    private const string Unrepresentable = "16777217";

    private static List<AttributeValue> Vec(string value) => [N(value)];

    private static async Task<TableDescription> WaitVectorTableActive(IAmazonDynamoDB ddb, string table, double timeoutS = 240)
    {
        var clock = FidelityTime.StartNew();
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        var tbl = (await ddb.DescribeTableAsync(table)).Table;
        Ensure(
            tbl.VectorIndexes is { Count: > 0 },
            "CreateTable accepted VectorIndexes but DescribeTable reports none - the vector index was silently dropped");
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            tbl = (await ddb.DescribeTableAsync(table)).Table;
            if (tbl.TableStatus == TableStatus.ACTIVE && (tbl.VectorIndexes ?? []).All(v => v.IndexStatus == IndexStatus.ACTIVE))
            {
                return tbl;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail(
            $"table/vector index never became ACTIVE: {Py.Repr(tbl.TableStatus.Value)} "
            + $"{Py.Repr((tbl.VectorIndexes ?? []).Select(v => (v.IndexName, v.IndexStatus?.Value)).ToList())}");
    }

    private static async Task<SearchVectorsResponse> SearchUntil(IAmazonDynamoDB ddb, string table, string index, int topK, string query, int wantCount, double timeoutS = 90)
    {
        var clock = FidelityTime.StartNew();
        SearchVectorsResponse? response = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            response = await ddb.SearchVectorsAsync(new SearchVectorsRequest
            {
                TableName = table,
                IndexName = index,
                TopK = topK,
                SearchVector = Vec(query),
                ProjectionExpression = "pk, emb",
            });
            if ((response.SearchResults ?? []).Count == wantCount)
            {
                return response;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
        }

        throw Fail($"never saw {wantCount} search results within {timeoutS}s, last: {Py.Repr(response?.SearchResults?.Select(r => r.Item).ToList())}");
    }

    /// <summary>
    /// A DOT_PRODUCT index keeps a float32 copy of the embedding for scoring and projection: an
    /// integer above 2**24 rounds to the nearest float32, matching a neighbor already at that
    /// value, while GetItem on the base table keeps the full integer written.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.search-vectors-dot-product-score-rounds-integer-above-2-24-to-float32")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbvecf32-");

        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            VectorIndexes =
            [
                new VectorIndex
                {
                    IndexName = "idx",
                    VectorAttribute = new VectorAttributeDefinition { AttributeName = "emb" },
                    Dimensions = 1,
                    DistanceFunction = VectorDistanceFunction.DOT_PRODUCT,
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await WaitVectorTableActive(ddb, table);

        await ddb.PutItemAsync(table, Item(("pk", S("unrepresentable")), ("emb", new AttributeValue { L = Vec(Unrepresentable) })));
        await ddb.PutItemAsync(table, Item(("pk", S("unrounded")), ("emb", new AttributeValue { L = Vec(Unrounded) })));

        var response = await SearchUntil(ddb, table, "idx", topK: 2, query: "1.0", wantCount: 2);
        // The SearchVectors response model has no NextToken member at all in the .NET SDK, so the
        // Python's "no NextToken" assertion holds by construction.
        var results = response.SearchResults;
        Ensure(results.Count == 2, $"TopK=2 over 2 items returned {results.Count}, expected both back");

        var byPk = results.ToDictionary(r => r.Item["pk"].S, StringComparer.Ordinal);
        Ensure(
            byPk.Keys.ToHashSet().SetEquals(["unrepresentable", "unrounded"]),
            $"SearchVectors returned pks {Py.Repr(byPk.Keys.Order(StringComparer.Ordinal).ToList())}, expected both seeded items");

        var unrepresentableScore = byPk["unrepresentable"].Score;
        var unroundedScore = byPk["unrounded"].Score;
        Ensure(
            Py.Repr(unrepresentableScore) == "16777216.0",
            $"item written as {Unrepresentable} scored {Py.Repr(unrepresentableScore)}, expected the "
            + $"float32-rounded dot product 16777216.0 (the nearest float32 to {Unrepresentable})");
        Ensure(
            Py.Repr(unroundedScore) == "16777216.0",
            $"item written as {Unrounded} scored {Py.Repr(unroundedScore)}, expected the exact dot "
            + $"product 16777216.0 ({Unrounded} is itself representable in float32)");
        Ensure(
            unrepresentableScore == unroundedScore,
            $"a value float32 cannot represent ({Unrepresentable}) scored "
            + $"{Py.Repr(unrepresentableScore)}, a different value from its float32-exact neighbor "
            + $"({Unrounded}) at {Py.Repr(unroundedScore)} - the index copy should have collapsed them");

        var projectedUnrepresentable = byPk["unrepresentable"].Item["emb"].L[0].N;
        var projectedUnrounded = byPk["unrounded"].Item["emb"].L[0].N;
        Ensure(
            projectedUnrepresentable == "16777216.0",
            $"SearchVectors projected the vector attribute written as {Unrepresentable} as "
            + $"{Py.Repr(projectedUnrepresentable)}, expected the float32-rounded '16777216.0'");
        Ensure(
            projectedUnrounded == "16777216.0",
            $"SearchVectors projected the vector attribute written as {Unrounded} as "
            + $"{Py.Repr(projectedUnrounded)}, expected '16777216.0'");

        var storedUnrepresentable = (await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("unrepresentable"))),
            ConsistentRead = true,
        })).Item["emb"].L[0].N;
        var storedUnrounded = (await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("unrounded"))),
            ConsistentRead = true,
        })).Item["emb"].L[0].N;
        Ensure(
            storedUnrepresentable == Unrepresentable,
            $"GetItem on the base table returned {Py.Repr(storedUnrepresentable)} for the vector "
            + $"component written as {Unrepresentable}, expected the full-precision "
            + $"{Py.Repr(Unrepresentable)} unrounded");
        Ensure(
            storedUnrounded == Unrounded,
            $"GetItem on the base table returned {Py.Repr(storedUnrounded)} for the vector component "
            + $"written as {Unrounded}, expected the full-precision {Py.Repr(Unrounded)}");
        Ensure(
            storedUnrepresentable != projectedUnrepresentable,
            $"GetItem on the base table ({Py.Repr(storedUnrepresentable)}) and SearchVectors' projected "
            + $"vector ({Py.Repr(projectedUnrepresentable)}) agreed for a value float32 cannot represent "
            + "exactly - expected the base table to keep full precision while the index's "
            + "projected copy carries the float32-rounded value");
    }
}

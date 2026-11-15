// Translated from behavioral/dynamodb/check_search_vectors_dot_product_order_and_score_precision.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.search-vectors-dot-product-order-and-score-precision</summary>
[Category("AwsEvaluationSuite")]
public sealed class SearchVectorsDotProductOrderAndScorePrecision
{
    private const string Check = "search-vectors-dot-product-order-and-score-precision";

    private static readonly (string Pk, double Value)[] Items = [("a", 2.0), ("b", 0.0), ("c", -3.0), ("d", 0.1)];

    /// <summary>Python's <c>str(value)</c> of a float.</summary>
    private static List<AttributeValue> Vec(double value) => [N(Py.Repr(value))];

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
            $"table/vector index never became ACTIVE: {Py.Repr(tbl.TableStatus.Value)} " +
            $"{Py.Repr((tbl.VectorIndexes ?? []).Select(v => (v.IndexName, v.IndexStatus?.Value)).ToList())}");
    }

    private static async Task<SearchVectorsResponse> SearchUntil(IAmazonDynamoDB ddb, string table, string index, int topK, double query, int wantCount, double timeoutS = 90)
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
    /// SearchVectors on a DOT_PRODUCT index ranks the highest score first, including negative
    /// scores, and a TopK above the item count returns every item with no NextToken or
    /// LastEvaluatedKey. Score reflects float32 math; a projected vector keeps full precision.
    /// </summary>
    [Test]
    [KnownFidelityGap(FidelityGap.DynamoDbVectorSearch, "CreateTable ignores VectorIndexes and SearchVectors is not emulated")]
    [DisplayName("dynamodb.search-vectors-dot-product-order-and-score-precision")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbvecprec-");
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
                    DistanceFunction = new VectorDistanceFunction("DOT_PRODUCT"),
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await WaitVectorTableActive(ddb, table);
        foreach (var (pk, value) in Items)
        {
            await ddb.PutItemAsync(table, Item(("pk", S(pk)), ("emb", new AttributeValue { L = Vec(value) })));
        }

        // The SearchVectors response model has no NextToken or LastEvaluatedKey member at all in
        // the .NET SDK, so their absence holds by construction.
        var response = await SearchUntil(ddb, table, "idx", topK: 10, query: 1.0, wantCount: Items.Length);
        var results = response.SearchResults;
        Ensure(results.Count == Items.Length, $"TopK=10 over {Items.Length} items returned {results.Count}, expected every item back");
        var ordered = results.Select(r => (Pk: r.Item["pk"].S, Score: r.Score)).ToList();
        var orderedPks = ordered.Select(o => o.Pk).ToList();
        Ensure(orderedPks.SequenceEqual(["a", "d", "b", "c"]), $"DOT_PRODUCT must rank the highest score first, got {Py.Repr(orderedPks)}");
        var scores = ordered.Select(o => o.Score).ToList();
        Ensure(scores.SequenceEqual(scores.OrderDescending()), $"DOT_PRODUCT scores were not descending: {Py.Repr(scores)}");
        Ensure(Py.Repr(scores[0]) == "2.0", $"item 'a' scored {Py.Repr(scores[0])}, expected 2.0");
        Ensure(
            Py.Repr(scores[1]) == "0.10000000149011612",
            $"item 'd' scored {Py.Repr(scores[1])}, expected the float32-rounded dot product of 0.1 with the query vector");
        Ensure(Py.Repr(scores[2]) == "0.0", $"item 'b' scored {Py.Repr(scores[2])}, expected 0.0");
        Ensure(Py.Repr(scores[3]) == "-3.0", $"item 'c' scored {Py.Repr(scores[3])}, expected the negative score -3.0");
        var byPk = results.ToDictionary(r => r.Item["pk"].S, r => r.Item, StringComparer.Ordinal);
        var searchedEmb = byPk["d"]["emb"].L[0].N;
        Ensure(searchedEmb == "0.1", $"SearchVectors projected the vector attribute as {Py.Repr(searchedEmb)}, expected the full-precision '0.1' as written");
        var stored = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("d"))), ConsistentRead = true })).Item;
        var storedEmb = stored["emb"].L[0].N;
        Ensure(storedEmb == "0.1", $"GetItem on the base table returned {Py.Repr(storedEmb)} for the written vector component, expected the full-precision '0.1' as written");
        Ensure(
            storedEmb == searchedEmb,
            $"GetItem returned {Py.Repr(storedEmb)} but SearchVectors projected {Py.Repr(searchedEmb)} for the same written vector component");
    }
}

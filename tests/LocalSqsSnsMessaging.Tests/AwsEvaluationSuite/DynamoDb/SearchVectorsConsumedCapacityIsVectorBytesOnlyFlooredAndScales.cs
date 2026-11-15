// Translated from behavioral/dynamodb/check_search_vectors_consumed_capacity_is_vector_bytes_only_floored_and_scales.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.search-vectors-consumed-capacity-is-vector-bytes-only-floored-and-scales</summary>
/// <remarks>
/// boto3's model gives SearchVectors a flat <c>ConsumedCapacity.VectorSearchRequestBytes</c>; the
/// .NET SDK's (4.1.0) nests it under <c>ConsumedCapacity.VectorIndexes</c>, so the SearchVectors
/// calls go through <c>ctx.DynamoDbRaw</c> and read the response as boto3 does.
/// </remarks>
[Category("AwsEvaluationSuite")]
public sealed class SearchVectorsConsumedCapacityIsVectorBytesOnlyFlooredAndScales
{
    private const string Check = "search-vectors-consumed-capacity-is-vector-bytes-only-floored-and-scales";
    private static readonly double[] QuerySmall = [1.0, 0.0, 0.0, 0.0];
    private const int DimsBig = 128;
    private const int NItems = 25;

    /// <summary>Python's <c>repr(float(x))</c>.</summary>
    private static string FloatRepr(double x) => Py.Repr(x);

    private static List<AttributeValue> Vec(IEnumerable<double> values) => [.. values.Select(x => N(FloatRepr(x)))];

    private static List<Dictionary<string, string>> VecJson(IEnumerable<double> values) =>
        [.. values.Select(x => new Dictionary<string, string> { ["N"] = FloatRepr(x) })];

    /// <summary>A SearchVectors call as boto3 sends it, answered as boto3 parses it.</summary>
    private static async Task<JsonElement> SearchVectors(
        FidelityContext ctx, string table, string index, int topK, IEnumerable<double> query, string returnConsumedCapacity, string? projectionExpression = null)
    {
        var body = new Dictionary<string, object>
        {
            ["TableName"] = table,
            ["IndexName"] = index,
            ["TopK"] = topK,
            ["SearchVector"] = VecJson(query),
            ["ReturnConsumedCapacity"] = returnConsumedCapacity,
        };
        if (projectionExpression is not null)
        {
            body["ProjectionExpression"] = projectionExpression;
        }

        var response = await ctx.DynamoDbRaw("SearchVectors", JsonSerializer.Serialize(body));
        if ((int)response.StatusCode >= 300)
        {
            throw Fail($"An error occurred ({response.ErrorCode}) when calling the SearchVectors operation: {response.ErrorMessage}");
        }

        return response.Json!.Value;
    }

    private static async Task<JsonElement> SearchUntil(
        FidelityContext ctx, string table, string index, int topK, IEnumerable<double> query, Func<int, bool> done, double timeoutS = 90, string returnConsumedCapacity = "TOTAL")
    {
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var output = await SearchVectors(ctx, table, index, topK, query, returnConsumedCapacity);
            var results = output.TryGetProperty("SearchResults", out var r) ? r.GetArrayLength() : 0;
            if (done(results))
            {
                return output.TryGetProperty("ConsumedCapacity", out var cc) ? cc : JsonDocument.Parse("{}").RootElement;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
        }

        throw Fail($"{index}: TopK={topK} never reached the expected result count in time");
    }

    private static async Task<TableDescription> WaitVectorTableActive(IAmazonDynamoDB ddb, string table, double timeoutS = 240)
    {
        var clock = FidelityTime.StartNew();
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        var tbl = (await ddb.DescribeTableAsync(table)).Table;
        Ensure(
            tbl.VectorIndexes is { Count: > 0 },
            "CreateTable accepted VectorIndexes but DescribeTable reports none - the vector indexes were silently dropped");
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
            $"table/vector indexes never became ACTIVE: {Py.Repr(tbl.TableStatus.Value)} " +
            $"{Py.Repr((tbl.VectorIndexes ?? []).Select(v => (v.IndexName, v.IndexStatus?.Value)).ToList())}");
    }

    private static double Bytes(JsonElement consumedCapacity) => consumedCapacity.GetProperty("VectorSearchRequestBytes").GetDouble();

    /// <summary>
    /// SearchVectors ConsumedCapacity carries only VectorSearchRequestBytes, floored at 1024,
    /// and that figure grows with TopK and with returning the searched vector attribute itself.
    /// </summary>
    [Test]
    [KnownFidelityGap(FidelityGap.DynamoDbVectorSearch, "CreateTable ignores VectorIndexes and SearchVectors is not emulated")]
    [DisplayName("dynamodb.search-vectors-consumed-capacity-is-vector-bytes-only-floored-and-scales")]
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
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            VectorIndexes =
            [
                new VectorIndex
                {
                    IndexName = "idx-small",
                    VectorAttribute = new VectorAttributeDefinition { AttributeName = "emb_small" },
                    Dimensions = 4,
                    DistanceFunction = VectorDistanceFunction.COSINE,
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
                new VectorIndex
                {
                    IndexName = "idx-big",
                    VectorAttribute = new VectorAttributeDefinition { AttributeName = "emb_big" },
                    Dimensions = DimsBig,
                    DistanceFunction = VectorDistanceFunction.COSINE,
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await WaitVectorTableActive(ddb, table);
        var queryBig = Enumerable.Range(0, DimsBig).Select(j => j == 0 ? 1.0 : 0.0).ToList();
        for (var i = 0; i < NItems; i++)
        {
            var big = Enumerable.Range(0, DimsBig).Select(j => j == 0 ? 1.0 : 0.0).ToArray();
            big[1] = i * 0.001;
            await ddb.PutItemAsync(table, Item(
                ("pk", S($"v{i}")),
                ("emb_small", new AttributeValue { L = Vec(QuerySmall) }),
                ("emb_big", new AttributeValue { L = Vec(big) })));
        }

        // Shape and floor: a minimal search on a 4-dim index reports exactly one key, at 1024.
        var floorCc = await SearchUntil(ctx, table, "idx-small", 1, QuerySmall, res => res >= 1);
        var floorKeys = floorCc.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToList();
        Ensure(
            floorKeys.SequenceEqual(["VectorSearchRequestBytes"]),
            $"SearchVectors ConsumedCapacity keys were {Py.Repr(floorKeys)}, expected only VectorSearchRequestBytes (no CapacityUnits, TableName or Table)");
        var floorBytes = Bytes(floorCc);
        Ensure(floorBytes == 1024.0, $"VectorSearchRequestBytes was {Py.Repr(floorBytes)}, expected 1024.0 (the 1 KB minimum)");

        // ReturnConsumedCapacity=INDEXES reports the same flat shape as TOTAL for SearchVectors.
        var indexesCc = await SearchUntil(ctx, table, "idx-small", 1, QuerySmall, res => res >= 1, returnConsumedCapacity: "INDEXES");
        Ensure(
            indexesCc.EnumerateObject().Count() == 1 && indexesCc.TryGetProperty("VectorSearchRequestBytes", out var ib) && ib.GetDouble() == 1024.0,
            $"ReturnConsumedCapacity=INDEXES on SearchVectors returned {Py.Repr(indexesCc)}, expected only VectorSearchRequestBytes at the 1024.0 floor");

        // TopK growth, same index and query: more candidates scored means more bytes.
        var topk1Cc = await SearchUntil(ctx, table, "idx-big", 1, queryBig, res => res >= 1);
        var topknCc = await SearchUntil(ctx, table, "idx-big", NItems, queryBig, res => res == NItems);
        var bytesTopk1 = Bytes(topk1Cc);
        var bytesTopkn = Bytes(topknCc);
        Ensure(bytesTopkn > bytesTopk1, $"TopK={NItems} reported {Py.Repr(bytesTopkn)} bytes, not more than TopK=1's {Py.Repr(bytesTopk1)}");

        // Returning the searched vector attribute itself raises the meter over a projection that
        // withholds it, same TopK and query throughout.
        var pkOnly = Bytes((await SearchVectors(ctx, table, "idx-big", 1, queryBig, "TOTAL", "pk")).GetProperty("ConsumedCapacity"));
        var withVector = Bytes((await SearchVectors(ctx, table, "idx-big", 1, queryBig, "TOTAL", "pk, emb_big")).GetProperty("ConsumedCapacity"));
        Ensure(
            withVector > pkOnly,
            $"projecting in the vector attribute \"emb_big\" left VectorSearchRequestBytes at {Py.Repr(withVector)}, not more than {Py.Repr(pkOnly)} without it");
    }
}

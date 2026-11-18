// Translated from behavioral/dynamodb/check_vector_index_4096_dimensions_and_hash_inline_filter_search_schema_described_back.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.vector-index-4096-dimensions-and-hash-inline-filter-search-schema-described-back</summary>
[Category("AwsEvaluationSuite")]
public sealed class VectorIndex4096DimensionsAndHashInlineFilterSearchSchemaDescribedBack
{
    private const string Check = "vector-index-4096-dimensions-and-hash-inline-filter-search-schema-described-back";

    /// <summary>
    /// DescribeTable reports Dimensions 4096 for a vector index created at that size, and a
    /// SearchSchema combining a HASH and an INLINE_FILTER element round-trips both, in order.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.vector-index-4096-dimensions-and-hash-inline-filter-search-schema-described-back")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbvecdim-");
        static List<SearchSchemaElement> SearchSchema() =>
        [
            new SearchSchemaElement { AttributeName = "tenant", SearchSchemaElementType = SearchSchemaElementType.HASH },
            new SearchSchemaElement { AttributeName = "cat", SearchSchemaElementType = SearchSchemaElementType.INLINE_FILTER },
        ];
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("tenant", ScalarAttributeType.S),
                new AttributeDefinition("cat", ScalarAttributeType.S),
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            VectorIndexes =
            [
                new VectorIndex
                {
                    IndexName = "vec-idx",
                    VectorAttribute = new VectorAttributeDefinition { AttributeName = "embedding" },
                    Dimensions = 4096,
                    DistanceFunction = VectorDistanceFunction.COSINE,
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                    SearchSchema = SearchSchema(),
                },
            ],
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        var tbl = await WaitVectorIndexActive(ddb, table);
        var idx = tbl.VectorIndexes[0];
        Ensure(idx.Dimensions == 4096, $"expected Dimensions 4096, got {Py.Repr(idx.Dimensions)}");
        var expected = Schema(SearchSchema());
        var got = Schema(idx.SearchSchema);
        Ensure(Py.Equal(got, expected), $"expected SearchSchema {Py.Repr(expected)} in order, got {Py.Repr(got)}");
        Ensure(idx.DistanceFunction == VectorDistanceFunction.COSINE, $"expected COSINE, got {Py.Repr(idx.DistanceFunction?.Value)}");
    }

    private static List<Dictionary<string, string?>>? Schema(List<SearchSchemaElement>? schema) =>
        schema?.Select(e => new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["AttributeName"] = e.AttributeName,
            ["SearchSchemaElementType"] = e.SearchSchemaElementType?.Value,
        }).ToList();

    private static async Task<TableDescription> WaitVectorIndexActive(IAmazonDynamoDB ddb, string table, double timeoutS = 240)
    {
        var clock = FidelityTime.StartNew();
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        var tbl = (await ddb.DescribeTableAsync(table)).Table;
        Ensure(tbl.VectorIndexes is { Count: > 0 }, "CreateTable accepted VectorIndexes but DescribeTable reports none");
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
            $"table/vector index never became ACTIVE: {Py.Repr(tbl.TableStatus?.Value)} "
            + Py.Repr((tbl.VectorIndexes ?? []).Select(v => (v.IndexName, v.IndexStatus?.Value)).ToList()));
    }
}

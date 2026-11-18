// Translated from behavioral/dynamodb/check_deep_dynamodb_1_ddbvec_search_vector_dimension_mismatch_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.deep-dynamodb-1-ddbvec-search-vector-dimension-mismatch-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeepDynamodb1DdbvecSearchVectorDimensionMismatchRejected
{
    private const string CheckId = "deep-dynamodb-1-ddbvec-search-vector-dimension-mismatch-rejected";
    private static readonly double[][] WrongVectors = [[1, 2, 3], [1, 2, 3, 4, 5]];

    /// <summary>
    /// A search vector shorter or longer than the index's Dimensions is a ValidationException.
    /// The model allows 1..4096 elements; the service checks against the index's declared Dimensions.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.deep-dynamodb-1-ddbvec-search-vector-dimension-mismatch-rejected")]
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
            VectorIndexes = [Vi("idx-euc", "EUCLIDEAN"), Vi("idx-dot", "DOT_PRODUCT")],
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await WaitVectorTableActive(ddb, table);
        await ddb.PutItemAsync(table, Item(("pk", S("only")), ("emb", new AttributeValue { L = Vec([1, 2, 3, 4]) })));
        foreach (var idx in new[] { "idx-euc", "idx-dot" })
        {
            var got = new List<string>();
            var clock = FidelityTime.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(90))
            {
                got = ((await ddb.SearchVectorsAsync(new SearchVectorsRequest
                {
                    TableName = table,
                    IndexName = idx,
                    TopK = 2,
                    SearchVector = Vec([1, 2, 3, 4]),
                })).SearchResults ?? []).Select(r => r.Item["pk"].S).ToList();
                if (got.Count > 0)
                {
                    break;
                }

                await FidelityTime.Delay(TimeSpan.FromSeconds(1));
            }

            Ensure(got.SequenceEqual(["only"]), $"positive control: 4-d search on {idx} returned {Py.Repr(got)}");
            foreach (var wrong in WrongVectors)
            {
                var err = await Error(
                    () => ddb.SearchVectorsAsync(new SearchVectorsRequest
                    {
                        TableName = table,
                        IndexName = idx,
                        TopK = 2,
                        SearchVector = Vec(wrong),
                    }),
                    $"{wrong.Length}-element search vector on 4-d {idx}");
                Ensure(
                    err.Code == "ValidationException",
                    $"{idx}, {wrong.Length}-d: expected ValidationException, got {Py.Repr(ErrDict(err))}");
                var expectedMsg = $"Input search vector dimension {wrong.Length} does not match vector "
                    + "index dimension 4";
                Ensure(
                    err.Message == expectedMsg,
                    $"{idx}, {wrong.Length}-d: expected message {Py.Repr(expectedMsg)}, "
                    + $"got {Py.Repr(err.Message)}");
            }
        }
    }
    /// <summary><c>[{'N': repr(float(x))} for x in values]</c>.</summary>
    private static List<AttributeValue> Vec(IEnumerable<double> values) => values.Select(x => N(PyFloat(x))).ToList();

    private static string PyFloat(double x)
    {
        var text = x.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.', StringComparison.Ordinal) || text.Contains('E', StringComparison.Ordinal) ? text : text + ".0";
    }

    private static Dictionary<string, object?> ErrDict(AwsError err) => new(StringComparer.Ordinal) { ["Code"] = err.Code, ["Message"] = err.Message };

    private static async Task<AwsError> Error<T>(Func<Task<T>> call, string what)
    {
        try
        {
            await call();
        }
        catch (AmazonServiceException ex)
        {
            return AwsError.From(ex);
        }
        catch (Exception ex)
        {
            throw new FidelityAssertionException($"{what}: raised a non-service error {ex}", ex);
        }

        throw Fail($"{what}: the call was ACCEPTED; real AWS rejects it");
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
}

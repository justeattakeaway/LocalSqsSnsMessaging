// Translated from behavioral/dynamodb/check_deep_dynamodb_1_ddbvec_unknown_index_name_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.deep-dynamodb-1-ddbvec-unknown-index-name-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeepDynamodb1DdbvecUnknownIndexNameRejected
{
    private const string CheckId = "deep-dynamodb-1-ddbvec-unknown-index-name-rejected";
    private static readonly double[] Query = [1.0, 0.0, 0.0, 0.0];

    /// <summary>
    /// SearchVectors naming a missing index (a near-miss name, a case variant) raises ValidationException with the
    /// exact message naming it, while the three real indexes answer before and remain afterwards.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.deep-dynamodb-1-ddbvec-unknown-index-name-rejected")]
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
        await WaitVectorTableActive(ddb, table);
        await ddb.PutItemAsync(table, Item(("pk", S("only")), ("emb", new AttributeValue { L = Vec(Query) })));

        async Task Control()
        {
            foreach (var idx in new[] { "idx-cos", "idx-euc", "idx-dot" })
            {
                var got = new List<string>();
                var clock = FidelityTime.StartNew();
                while (clock.Elapsed < TimeSpan.FromSeconds(90))
                {
                    got = ((await ddb.SearchVectorsAsync(new SearchVectorsRequest
                    {
                        TableName = table,
                        IndexName = idx,
                        TopK = 3,
                        SearchVector = Vec(Query),
                    })).SearchResults ?? []).Select(r => r.Item["pk"].S).ToList();
                    if (got.Count > 0)
                    {
                        break;
                    }

                    await FidelityTime.Delay(TimeSpan.FromSeconds(1));
                }

                Ensure(got.SequenceEqual(["only"]), $"positive control: {idx} returned {Py.Repr(got)}, expected [only]");
            }
        }

        await Control();
        foreach (var bad in new[] { "idx-cosine", "IDX-COS" })
        {
            var err = await Error(
                () => ddb.SearchVectorsAsync(new SearchVectorsRequest
                {
                    TableName = table,
                    IndexName = bad,
                    TopK = 3,
                    SearchVector = Vec(Query),
                }),
                $"SearchVectors on nonexistent index {Py.Repr(bad)}");
            Ensure(err.Code == "ValidationException", $"index {Py.Repr(bad)}: expected ValidationException, got {Py.Repr(ErrDict(err))}");
            var expectedMsg = $"The table does not have the specified index: {bad}";
            Ensure(err.Message == expectedMsg, $"index {Py.Repr(bad)}: expected message {Py.Repr(expectedMsg)}, got {Py.Repr(err.Message)}");
        }

        // Effect absent: the refusals created nothing and the real indexes are untouched.
        var names = ((await ddb.DescribeTableAsync(table)).Table.VectorIndexes ?? []).Select(v => v.IndexName).Order(StringComparer.Ordinal).ToList();
        Ensure(names.SequenceEqual(["idx-cos", "idx-dot", "idx-euc"]), $"vector indexes after refusals: {Py.Repr(names)}");
        await Control();
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

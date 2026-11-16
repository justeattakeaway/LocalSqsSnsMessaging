// Translated from behavioral/dynamodb/check_deep_dynamodb_1_ddbvec_hash_schema_search_without_condition_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.deep-dynamodb-1-ddbvec-hash-schema-search-without-condition-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeepDynamodb1DdbvecHashSchemaSearchWithoutConditionRejected
{
    private const string CheckId = "deep-dynamodb-1-ddbvec-hash-schema-search-without-condition-rejected";
    private static readonly double[] Query = [0.0, 0.0, 0.0, 0.0];

    /// <summary>
    /// A HASH search-schema vector index returns only the conditioned tenant's items, nearest-first.
    ///
    /// SearchVectors without a SearchConditionExpression on it is a ValidationException, not a
    /// cross-tenant result.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.deep-dynamodb-1-ddbvec-hash-schema-search-without-condition-rejected")]
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
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("tenant", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            VectorIndexes =
            [
                new VectorIndex
                {
                    IndexName = "idx-tenant",
                    VectorAttribute = new VectorAttributeDefinition { AttributeName = "emb" },
                    Dimensions = 4,
                    DistanceFunction = "EUCLIDEAN",
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                    SearchSchema = [new SearchSchemaElement { AttributeName = "tenant", SearchSchemaElementType = SearchSchemaElementType.HASH }],
                },
            ],
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await WaitVectorTableActive(ddb, table);

        // Tenant t2 holds the vectors nearest the origin; t1's are farther out.
        var items = new (string Pk, string Tenant, double X)[]
        {
            ("t1-a", "t1", 3.0), ("t1-b", "t1", 5.0), ("t1-c", "t1", 4.0),
            ("t2-a", "t2", 0.0), ("t2-b", "t2", 1.0),
        };
        foreach (var (pk, tenant, x) in items)
        {
            await ddb.PutItemAsync(table, Item(("pk", S(pk)), ("tenant", S(tenant)), ("emb", new AttributeValue { L = Vec([x, 0, 0, 0]) })));
        }

        async Task<List<SearchResultItem>> Tenant(string t) =>
            (await ddb.SearchVectorsAsync(new SearchVectorsRequest
            {
                TableName = table,
                IndexName = "idx-tenant",
                TopK = 10,
                SearchVector = Vec(Query),
                SearchConditionExpression = "tenant = :t",
                ExpressionAttributeValues = Item((":t", S(t))),
            })).SearchResults ?? [];

        var got = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(90))
        {
            got = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var t in new[] { "t1", "t2" })
            {
                got[t] = (await Tenant(t)).Select(r => r.Item["pk"].S).ToList();
            }

            if (got["t1"].Count == 3 && got["t2"].Count == 2)
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
        }

        Ensure(
            Py.Equal(got, new Dictionary<string, List<string>> { ["t1"] = ["t1-a", "t1-c", "t1-b"], ["t2"] = ["t2-a", "t2-b"] }),
            $"positive control: per-tenant searches returned {Py.Repr(got)}");

        var err = await Error(
            () => ddb.SearchVectorsAsync(new SearchVectorsRequest
            {
                TableName = table,
                IndexName = "idx-tenant",
                TopK = 10,
                SearchVector = Vec(Query),
            }),
            "SearchVectors with no condition on a HASH-partitioned vector index");
        Ensure(err.Code == "ValidationException", $"expected ValidationException, got {Py.Repr(ErrDict(err))}");
        const string expectedMsg = "SearchConditionExpression must be provided when SearchSchema has a HASH key";
        Ensure(err.Message == expectedMsg, $"expected message {Py.Repr(expectedMsg)}, got {Py.Repr(err.Message)}");

        // Unchanged state: the partitions still answer exactly as before.
        var again = (await Tenant("t1")).Select(r => r.Item["pk"].S).ToList();
        Ensure(again.SequenceEqual(["t1-a", "t1-c", "t1-b"]), $"t1 after the refusal returned {Py.Repr(again)}");
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
}

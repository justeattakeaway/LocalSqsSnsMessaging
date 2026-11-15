// Translated from behavioral/dynamodb/check_deep_dynamodb_1_ddbvec_inline_filter_range_comparator_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.deep-dynamodb-1-ddbvec-inline-filter-range-comparator-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeepDynamodb1DdbvecInlineFilterRangeComparatorRejected
{
    private const string CheckId = "deep-dynamodb-1-ddbvec-inline-filter-range-comparator-rejected";
    private static readonly double[] Query = [0.0, 0.0, 0.0, 0.0];

    /// <summary>
    /// SearchConditionExpression filters by equality on an INLINE_FILTER attribute but refuses &gt; and &lt;.
    ///
    /// The model says range operators are supported; real AWS rejects them with ValidationException.
    /// </summary>
    [Test]
    [KnownFidelityGap(FidelityGap.DynamoDbVectorSearch, "CreateTable ignores a vector index's SearchSchema, so an attribute defined only for it is refused as unused in the KeySchema")]
    [DisplayName("dynamodb.deep-dynamodb-1-ddbvec-inline-filter-range-comparator-rejected")]
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
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("price", ScalarAttributeType.N)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            VectorIndexes =
            [
                new VectorIndex
                {
                    IndexName = "idx-price",
                    VectorAttribute = new VectorAttributeDefinition { AttributeName = "emb" },
                    Dimensions = 4,
                    DistanceFunction = "EUCLIDEAN",
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                    SearchSchema = [new SearchSchemaElement { AttributeName = "price", SearchSchemaElementType = SearchSchemaElementType.INLINE_FILTER }],
                },
            ],
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await WaitVectorTableActive(ddb, table);
        for (var i = 0; i < 4; i++)
        {
            await ddb.PutItemAsync(table, Item(
                ("pk", S($"p{i}")),
                ("price", N((10 * i).ToString(CultureInfo.InvariantCulture))),
                ("emb", new AttributeValue { L = Vec([i, 0, 0, 0]) })));
        }

        var got = new List<string>();
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(90))
        {
            got = ((await ddb.SearchVectorsAsync(new SearchVectorsRequest
            {
                TableName = table,
                IndexName = "idx-price",
                TopK = 10,
                SearchVector = Vec(Query),
            })).SearchResults ?? []).Select(r => r.Item["pk"].S).ToList();
            if (got.Count == 4)
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
        }

        Ensure(got.SequenceEqual(["p0", "p1", "p2", "p3"]), $"unfiltered search returned {Py.Repr(got)}");

        var eq = (await ddb.SearchVectorsAsync(new SearchVectorsRequest
        {
            TableName = table,
            IndexName = "idx-price",
            TopK = 10,
            SearchVector = Vec(Query),
            SearchConditionExpression = "price = :p",
            ExpressionAttributeValues = Item((":p", N("20"))),
        })).SearchResults ?? [];
        Ensure(
            eq.Select(r => r.Item["pk"].S).SequenceEqual(["p2"]),
            $"positive control: price = 20 returned {Py.Repr(eq.Select(r => r.Item["pk"].S).ToList())}");

        foreach (var expr in new[] { "price > :p", "price < :p" })
        {
            var err = await Error(
                () => ddb.SearchVectorsAsync(new SearchVectorsRequest
                {
                    TableName = table,
                    IndexName = "idx-price",
                    TopK = 10,
                    SearchVector = Vec(Query),
                    SearchConditionExpression = expr,
                    ExpressionAttributeValues = Item((":p", N("15"))),
                }),
                $"SearchConditionExpression {Py.Repr(expr)} on an INLINE_FILTER attribute");
            Ensure(err.Code == "ValidationException", $"{Py.Repr(expr)}: expected ValidationException, got {Py.Repr(ErrDict(err))}");
            const string expectedMsg = "Invalid SearchConditionExpression: Invalid comparator used in "
                + "SearchConditionExpression";
            Ensure(err.Message == expectedMsg, $"{Py.Repr(expr)}: expected message {Py.Repr(expectedMsg)}, got {Py.Repr(err.Message)}");
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

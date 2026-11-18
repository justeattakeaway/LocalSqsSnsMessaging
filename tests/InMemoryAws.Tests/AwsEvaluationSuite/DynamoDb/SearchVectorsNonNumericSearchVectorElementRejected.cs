// Translated from behavioral/dynamodb/check_search_vectors_non_numeric_search_vector_element_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.search-vectors-non-numeric-search-vector-element-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class SearchVectorsNonNumericSearchVectorElementRejected
{
    private const string Check = "search-vectors-non-numeric-search-vector-element-rejected";

    private static readonly (string Code, string Message) BadSearchVector = (
        "ValidationException",
        "Search vector contains invalid values. All " +
        "values in the search vector must be a 32-bit floating-point number " +
        "attribute");

    /// <summary>Python's <c>str(v)</c> of each float.</summary>
    private static List<AttributeValue> Vec(IEnumerable<double> values) => [.. values.Select(v => N(Py.Repr(v)))];

    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }

    private static async Task ExpectError<T>(Func<Task<T>> fn, (string Code, string Message) expected)
        where T : class
    {
        var (gotCode, gotMessage) = await Error(fn);
        Ensure(gotCode == expected.Code, $"expected {Py.Repr(expected.Code)}, got {Py.Repr(gotCode)} ({Py.Repr(gotMessage)})");
        Ensure(gotMessage == expected.Message, $"expected message {Py.Repr(expected.Message)}, got {Py.Repr(gotMessage)}");
    }

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

    private static async Task<List<SearchResultItem>> SearchUntil(
        IAmazonDynamoDB ddb, string table, string index, int topK, List<AttributeValue> searchVector, int wantCount, double timeoutS = 90)
    {
        var clock = FidelityTime.StartNew();
        List<SearchResultItem> results = [];
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            results = (await ddb.SearchVectorsAsync(new SearchVectorsRequest
            {
                TableName = table,
                IndexName = index,
                TopK = topK,
                SearchVector = searchVector,
            })).SearchResults ?? [];
            if (results.Count == wantCount)
            {
                return results;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
        }

        throw Fail($"never saw {wantCount} search results within {timeoutS}s, last: {Py.Repr(results.Select(r => r.Item).ToList())}");
    }

    /// <summary>
    /// SearchVectors refuses a SearchVector with a non-number element with ValidationException,
    /// and a correct call against the same index succeeds before and after the refusal.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.search-vectors-non-numeric-search-vector-element-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbvecbad-");
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
                    Dimensions = 4,
                    DistanceFunction = VectorDistanceFunction.COSINE,
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await WaitVectorTableActive(ddb, table);
        await ddb.PutItemAsync(table, Item(("pk", S("p0")), ("emb", new AttributeValue { L = Vec([1.0, 0.0, 0.0, 0.0]) })));
        var before = await SearchUntil(ddb, table, "idx", topK: 1, searchVector: Vec([1.0, 0.0, 0.0, 0.0]), wantCount: 1);
        Ensure(before[0].Item["pk"].S == "p0", $"positive control before the refusal returned {Py.Repr(before.Select(r => r.Item).ToList())}, expected item 'p0'");
        List<AttributeValue> badVector = [N("1"), S("x"), N("2"), N("3")];
        await ExpectError(
            () => ddb.SearchVectorsAsync(new SearchVectorsRequest { TableName = table, IndexName = "idx", TopK = 1, SearchVector = badVector }),
            BadSearchVector);
        var after = await SearchUntil(ddb, table, "idx", topK: 1, searchVector: Vec([1.0, 0.0, 0.0, 0.0]), wantCount: 1);
        Ensure(after[0].Item["pk"].S == "p0", $"positive control after the refusal returned {Py.Repr(after.Select(r => r.Item).ToList())}, expected item 'p0' unaffected");
    }
}

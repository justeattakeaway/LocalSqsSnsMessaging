// Translated from behavioral/dynamodb/check_search_vectors_expression_attribute_name_mismatch_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.search-vectors-expression-attribute-name-mismatch-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class SearchVectorsExpressionAttributeNameMismatchRejected
{
    private const string CheckId = "search-vectors-expression-attribute-name-mismatch-rejected";
    private static readonly double[] Query = [1.0, 0.0];

    private static readonly (string Code, string Message) Undefined = (
        "ValidationException",
        "Invalid SearchConditionExpression: An expression attribute name used in the "
        + "document path is not defined; attribute name: #c");

    private static readonly (string Code, string Message) Unused = (
        "ValidationException",
        "Value provided in ExpressionAttributeNames unused in expressions: keys: {#unused}");

    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
    }

    private static async Task ExpectError<T>(Func<Task<T>> fn, (string Code, string Message) expected)
        where T : class
    {
        var (gotCode, gotMessage) = await Error(fn);
        Ensure(gotCode == expected.Code, $"expected {Py.Repr(expected.Code)}, got {Py.Repr(gotCode)} ({Py.Repr(gotMessage)})");
        Ensure(gotMessage == expected.Message, $"expected message {Py.Repr(expected.Message)}, got {Py.Repr(gotMessage)}");
    }

    private static List<AttributeValue> Vec(IEnumerable<double> values) => [.. values.Select(v => N(Py.Repr(v)))];

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

    private static async Task<List<SearchResultItem>> SearchUntil(IAmazonDynamoDB ddb, string table, string index, int wantCount, double timeoutS = 90)
    {
        var clock = FidelityTime.StartNew();
        List<SearchResultItem> results = [];
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            results = (await ddb.SearchVectorsAsync(new SearchVectorsRequest
            {
                TableName = table,
                IndexName = index,
                TopK = 10,
                SearchVector = Vec(Query),
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
    /// SearchVectors refuses a SearchConditionExpression naming an ExpressionAttributeNames
    /// placeholder with no entry, and refuses an ExpressionAttributeNames entry no placeholder
    /// references, each with its own ValidationException message; a correct call still succeeds.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.search-vectors-expression-attribute-name-mismatch-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbvecname-");

        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("cat", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            VectorIndexes =
            [
                new VectorIndex
                {
                    IndexName = "idx",
                    VectorAttribute = new VectorAttributeDefinition { AttributeName = "emb" },
                    Dimensions = 2,
                    DistanceFunction = VectorDistanceFunction.COSINE,
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                    SearchSchema = [new SearchSchemaElement { AttributeName = "cat", SearchSchemaElementType = new SearchSchemaElementType("INLINE_FILTER") }],
                },
            ],
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await WaitVectorTableActive(ddb, table);

        await ddb.PutItemAsync(table, Item(("pk", S("p0")), ("emb", new AttributeValue { L = Vec(Query) }), ("cat", S("x"))));

        var before = await SearchUntil(ddb, table, "idx", 1);
        Ensure(before[0].Item["pk"].S == "p0", $"positive control before the refusal returned {Py.Repr(before.Select(r => r.Item).ToList())}, expected item 'p0'");

        await ExpectError(
            () => ddb.SearchVectorsAsync(new SearchVectorsRequest
            {
                TableName = table,
                IndexName = "idx",
                TopK = 10,
                SearchVector = Vec(Query),
                SearchConditionExpression = "#c = :x",
                ExpressionAttributeValues = Item((":x", S("x"))),
            }),
            Undefined);
        await ExpectError(
            () => ddb.SearchVectorsAsync(new SearchVectorsRequest
            {
                TableName = table,
                IndexName = "idx",
                TopK = 10,
                SearchVector = Vec(Query),
                SearchConditionExpression = "cat = :x",
                ExpressionAttributeNames = Names(("#unused", "cat")),
                ExpressionAttributeValues = Item((":x", S("x"))),
            }),
            Unused);

        var after = await SearchUntil(ddb, table, "idx", 1);
        Ensure(after[0].Item["pk"].S == "p0", $"positive control after the refusal returned {Py.Repr(after.Select(r => r.Item).ToList())}, expected item 'p0' unaffected");
    }
}

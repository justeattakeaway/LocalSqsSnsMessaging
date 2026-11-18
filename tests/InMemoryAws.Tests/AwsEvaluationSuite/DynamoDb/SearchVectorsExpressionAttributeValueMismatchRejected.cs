// Translated from behavioral/dynamodb/check_search_vectors_expression_attribute_value_mismatch_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.search-vectors-expression-attribute-value-mismatch-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class SearchVectorsExpressionAttributeValueMismatchRejected
{
    private const string Check = "search-vectors-expression-attribute-value-mismatch-rejected";
    private static readonly double[] Query = [0.0, 0.0, 0.0, 0.0];

    private static readonly (string Code, string Message) Undefined = (
        "ValidationException",
        "Invalid SearchConditionExpression: An expression attribute value used in " +
        "expression is not defined; attribute value: :p");

    private static readonly (string Code, string Message) Unused = (
        "ValidationException",
        "Value provided in ExpressionAttributeValues unused in expressions: keys: {:unused}");

    private static List<AttributeValue> Vec(IEnumerable<double> values) => [.. values.Select(x => N(Py.Repr(x)))];

    private static async Task ExpectError<T>(Func<Task<T>> fn, (string Code, string Message) expected)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            Ensure(error.Code == expected.Code, $"expected error code {Py.Repr(expected.Code)}, got {Py.Repr(new Dictionary<string, object?> { ["Message"] = error.Message, ["Code"] = error.Code })}");
            Ensure(error.Message == expected.Message, $"expected message {Py.Repr(expected.Message)}, got {Py.Repr(error.Message)}");
            return;
        }

        throw Fail($"expected the call to be refused, but it succeeded: {result}");
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

    /// <summary>
    /// A SearchConditionExpression placeholder with no matching ExpressionAttributeValues entry,
    /// and an ExpressionAttributeValues entry no placeholder references, both refuse the search.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.search-vectors-expression-attribute-value-mismatch-rejected")]
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
                    DistanceFunction = VectorDistanceFunction.EUCLIDEAN,
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                    SearchSchema = [new SearchSchemaElement { AttributeName = "price", SearchSchemaElementType = new SearchSchemaElementType("INLINE_FILTER") }],
                },
            ],
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await WaitVectorTableActive(ddb, table);
        await ddb.PutItemAsync(table, Item(("pk", S("p0")), ("price", N("10")), ("emb", new AttributeValue { L = Vec([1, 0, 0, 0]) })));

        // Positive control: the attribute value is defined and used, so the filter applies.
        List<SearchResultItem> control = [];
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(90))
        {
            control = (await ddb.SearchVectorsAsync(new SearchVectorsRequest
            {
                TableName = table,
                IndexName = "idx-price",
                TopK = 10,
                SearchVector = Vec(Query),
                SearchConditionExpression = "price = :p",
                ExpressionAttributeValues = Item((":p", N("10"))),
            })).SearchResults ?? [];
            if (control.Count > 0)
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
        }

        var controlPks = control.Select(r => r.Item["pk"].S).ToList();
        Ensure(controlPks.SequenceEqual(["p0"]), $"positive control: price = 10 returned {Py.Repr(controlPks)}");
        await ExpectError(
            () => ddb.SearchVectorsAsync(new SearchVectorsRequest
            {
                TableName = table,
                IndexName = "idx-price",
                TopK = 10,
                SearchVector = Vec(Query),
                SearchConditionExpression = "price = :p",
            }),
            Undefined);
        await ExpectError(
            () => ddb.SearchVectorsAsync(new SearchVectorsRequest
            {
                TableName = table,
                IndexName = "idx-price",
                TopK = 10,
                SearchVector = Vec(Query),
                SearchConditionExpression = "price = :p",
                ExpressionAttributeValues = Item((":p", N("10")), (":unused", S("x"))),
            }),
            Unused);
    }
}

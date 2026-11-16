// Translated from behavioral/dynamodb/check_deep_dynamodb_1_ddbvec_condition_on_non_schema_attribute_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.deep-dynamodb-1-ddbvec-condition-on-non-schema-attribute-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeepDynamodb1DdbvecConditionOnNonSchemaAttributeRejected
{
    private const string CheckId = "deep-dynamodb-1-ddbvec-condition-on-non-schema-attribute-rejected";
    private static readonly double[] Query = [1.0, 0.0, 0.0, 0.0];

    /// <summary>
    /// SearchVectors filtering on the index's INLINE_FILTER attribute returns the matching item, while filtering
    /// on a projected attribute outside the SearchSchema raises ValidationException with the exact message.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.deep-dynamodb-1-ddbvec-condition-on-non-schema-attribute-rejected")]
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
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("cat", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            VectorIndexes =
            [
                new VectorIndex
                {
                    IndexName = "idx-cos",
                    VectorAttribute = new VectorAttributeDefinition { AttributeName = "emb" },
                    Dimensions = 4,
                    DistanceFunction = "COSINE",
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                    SearchSchema = [new SearchSchemaElement { AttributeName = "cat", SearchSchemaElementType = SearchSchemaElementType.INLINE_FILTER }],
                },
            ],
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await WaitVectorTableActive(ddb, table);
        foreach (var (pk, cat) in new[] { ("a", "x"), ("b", "y") })
        {
            await ddb.PutItemAsync(table, Item(("pk", S(pk)), ("emb", new AttributeValue { L = Vec(Query) }), ("cat", S(cat)), ("note", S($"n-{pk}"))));
        }

        var got = new List<string>();
        List<SearchResultItem> res = [];
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(90))
        {
            res = (await ddb.SearchVectorsAsync(new SearchVectorsRequest
            {
                TableName = table,
                IndexName = "idx-cos",
                TopK = 5,
                SearchVector = Vec(Query),
            })).SearchResults ?? [];
            got = res.Select(r => r.Item["pk"].S).Order(StringComparer.Ordinal).ToList();
            if (got.SequenceEqual(["a", "b"]))
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
        }

        Ensure(got.SequenceEqual(["a", "b"]), $"both items should be searchable, got {Py.Repr(got)}");
        Ensure(
            res.All(r => r.Item.TryGetValue("note", out var note) && note is not null),
            $"note is projected into the index and must be returned: {Py.Repr(res.Select(r => r.Item).ToList())}");

        var ok = (await ddb.SearchVectorsAsync(new SearchVectorsRequest
        {
            TableName = table,
            IndexName = "idx-cos",
            TopK = 5,
            SearchVector = Vec(Query),
            SearchConditionExpression = "cat = :v",
            ExpressionAttributeValues = Item((":v", S("x"))),
        })).SearchResults ?? [];
        Ensure(
            ok.Select(r => r.Item["pk"].S).SequenceEqual(["a"]),
            $"positive control: filtering on schema attribute cat returned {Py.Repr(ok.Select(r => r.Item).ToList())}");

        var err = await Error(
            () => ddb.SearchVectorsAsync(new SearchVectorsRequest
            {
                TableName = table,
                IndexName = "idx-cos",
                TopK = 5,
                SearchVector = Vec(Query),
                SearchConditionExpression = "note = :v",
                ExpressionAttributeValues = Item((":v", S("n-a"))),
            }),
            "SearchConditionExpression on non-schema attribute note");
        Ensure(err.Code == "ValidationException", $"expected ValidationException, got {Py.Repr(ErrDict(err))}");
        const string expectedMsg = "SearchConditionExpression must not contain any attributes that is not in "
            + "SearchSchema. Invalid attribute: note";
        Ensure(err.Message == expectedMsg, $"expected message {Py.Repr(expectedMsg)}, got {Py.Repr(err.Message)}");
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

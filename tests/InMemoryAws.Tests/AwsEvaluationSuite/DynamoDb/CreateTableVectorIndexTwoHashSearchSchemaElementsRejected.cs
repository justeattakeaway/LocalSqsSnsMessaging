// Translated from behavioral/dynamodb/check_create_table_vector_index_two_hash_search_schema_elements_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.create-table-vector-index-two-hash-search-schema-elements-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateTableVectorIndexTwoHashSearchSchemaElementsRejected
{
    private const string CheckId = "create-table-vector-index-two-hash-search-schema-elements-rejected";

    /// <summary>
    /// A vector index SearchSchema takes at most one HASH element; CreateTable refuses a second
    /// one with the exact ValidationException, and the table it would have created never exists.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.create-table-vector-index-two-hash-search-schema-elements-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;

        // Positive control: a single HASH element is accepted and described back.
        var control = FidelityContext.UniqueName("sim2real-behav-ddbvec2hash-control-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = control,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("tenant", ScalarAttributeType.S),
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            VectorIndexes =
            [
                new VectorIndex
                {
                    IndexName = "vec-idx",
                    VectorAttribute = new VectorAttributeDefinition { AttributeName = "embedding" },
                    Dimensions = 8,
                    DistanceFunction = "COSINE",
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                    SearchSchema = [new SearchSchemaElement { AttributeName = "tenant", SearchSchemaElementType = SearchSchemaElementType.HASH }],
                },
            ],
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(control));
        var controlTbl = await WaitVectorIndexActive(ddb, control);
        var controlSchema = SearchSchema(controlTbl.VectorIndexes[0].SearchSchema);
        Ensure(
            Py.Equal(controlSchema, SearchSchema([new SearchSchemaElement { AttributeName = "tenant", SearchSchemaElementType = SearchSchemaElementType.HASH }])),
            $"control: expected the single HASH element back, got {Py.Repr(controlSchema)}");

        // Two HASH elements: refused, and the table never exists.
        var rejected = FidelityContext.UniqueName("sim2real-behav-ddbvec2hash-rejected-");
        var (code, message) = await Error(() => ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = rejected,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("tenant", ScalarAttributeType.S),
                new AttributeDefinition("region", ScalarAttributeType.S),
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            VectorIndexes =
            [
                new VectorIndex
                {
                    IndexName = "vec-idx",
                    VectorAttribute = new VectorAttributeDefinition { AttributeName = "embedding" },
                    Dimensions = 8,
                    DistanceFunction = "COSINE",
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                    SearchSchema =
                    [
                        new SearchSchemaElement { AttributeName = "tenant", SearchSchemaElementType = SearchSchemaElementType.HASH },
                        new SearchSchemaElement { AttributeName = "region", SearchSchemaElementType = SearchSchemaElementType.HASH },
                    ],
                },
            ],
        }));
        Ensure(code == "ValidationException", $"two HASH elements gave {Py.Repr(code)}, not ValidationException");
        const string expectedMessage = "One or more parameter values were invalid: Value '2' at 'SearchSchema' failed to "
            + "satisfy constraint: Member must have HASH count less than or equal to 1";
        Ensure(message == expectedMessage, $"two HASH elements message was {Py.Repr(message)}");

        var (code2, message2) = await Error(() => ddb.DescribeTableAsync(rejected));
        Ensure(
            code2 == "ResourceNotFoundException",
            $"{rejected}: CreateTable was refused but DescribeTable returned {Py.Repr(code2)}, not "
            + "ResourceNotFoundException -- the table exists");
        var expected2 = $"Requested resource not found: Table: {rejected} not found";
        Ensure(message2 == expected2, $"{rejected}: unexpected DescribeTable message {Py.Repr(message2)}");
    }

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

    private static List<Dictionary<string, string?>> SearchSchema(List<SearchSchemaElement>? elements) =>
        (elements ?? []).Select(e => new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["AttributeName"] = e.AttributeName,
            ["SearchSchemaElementType"] = e.SearchSchemaElementType?.ToString(),
        }).ToList();

    private static async Task<TableDescription> WaitVectorIndexActive(IAmazonDynamoDB ddb, string table, int timeoutSeconds = 240)
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

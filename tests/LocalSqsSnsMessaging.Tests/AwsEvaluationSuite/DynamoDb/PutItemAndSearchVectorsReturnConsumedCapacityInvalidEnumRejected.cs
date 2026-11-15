// Translated from behavioral/dynamodb/check_put_item_and_search_vectors_return_consumed_capacity_invalid_enum_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.put-item-and-search-vectors-return-consumed-capacity-invalid-enum-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutItemAndSearchVectorsReturnConsumedCapacityInvalidEnumRejected
{
    private const string CheckId = "put-item-and-search-vectors-return-consumed-capacity-invalid-enum-rejected";

    private static readonly (string Code, string Message) ValidationPut = (
        "ValidationException",
        "1 validation error detected: Value 'BOGUS' at 'returnConsumedCapacity' failed to satisfy "
        + "constraint: Member must satisfy enum value set: [INDEXES, TOTAL, NONE]");

    private static readonly (string Code, string Message) ValidationSearch = (
        "ValidationException",
        "Value 'BOGUS' at 'returnConsumedCapacity' failed to satisfy constraint: Member must "
        + "satisfy enum value set: [INDEXES, TOTAL, NONE]");

    private static List<AttributeValue> Vec() => [N("1"), N("0"), N("0"), N("0")];

    private static async Task<(string? Code, string? Message)> Error(Func<Task<object>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be rejected, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }

    private static async Task ExpectError(Func<Task<object>> fn, (string Code, string Message) expected)
    {
        var got = await Error(fn);
        Ensure(got == expected, $"expected {Py.Repr(expected)}, got {Py.Repr(got)}");
    }

    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };

    private static async Task<TableDescription> WaitVectorTableActive(IAmazonDynamoDB ddb, string table, double timeoutS = 240)
    {
        var clock = FidelityTime.StartNew();
        var tbl = (await ddb.DescribeTableAsync(table)).Table;
        var indexes = new List<VectorIndexDescription>();
        while (clock.Elapsed.TotalSeconds < timeoutS)
        {
            tbl = (await ddb.DescribeTableAsync(table)).Table;
            indexes = tbl.VectorIndexes ?? [];
            if (tbl.TableStatus == TableStatus.ACTIVE && indexes.Count > 0
                && indexes.All(v => v.IndexStatus?.Value == "ACTIVE"))
            {
                return tbl;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail(
            $"table/vector index never became ACTIVE: {Py.Repr(tbl.TableStatus)} "
            + $"{Py.Repr(indexes.Select(v => (v.IndexName, v.IndexStatus)).ToList())}");
    }

    /// <summary>
    /// An out-of-enum ReturnConsumedCapacity is ValidationException on PutItem and SearchVectors
    /// alike, and a well-formed retry of each still performs its write or search correctly.
    /// </summary>
    [Test]
    [KnownFidelityGap(FidelityGap.DynamoDbVectorSearch, "Vector indexes are not emulated: the table never reports an ACTIVE vector index")]
    [DisplayName("dynamodb.put-item-and-search-vectors-return-consumed-capacity-invalid-enum-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbvecenum-");

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
                    DistanceFunction = "COSINE",
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await WaitVectorTableActive(ddb, table);

        await ExpectError(
            async () => await ddb.PutItemAsync(new PutItemRequest
            {
                TableName = table,
                Item = Item(("pk", S("v1")), ("emb", new AttributeValue { L = Vec() })),
                ReturnConsumedCapacity = "BOGUS",
            }),
            ValidationPut);
        var controlPut = await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = Item(("pk", S("v1")), ("emb", new AttributeValue { L = Vec() })),
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        });
        Ensure(
            controlPut.ConsumedCapacity?.TableName == table,
            $"the rejected PutItem must leave the table usable; the well-formed retry's ConsumedCapacity was {Py.Repr(controlPut.ConsumedCapacity?.TableName)}");
        var stored = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("v1"))), ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(stored, Item(("pk", S("v1")), ("emb", new AttributeValue { L = Vec() }))),
            $"the well-formed retry must have written the item, read back {Py.Repr(stored)}");

        await ExpectError(
            async () => await ddb.SearchVectorsAsync(new SearchVectorsRequest
            {
                TableName = table,
                IndexName = "idx",
                TopK = 1,
                SearchVector = Vec(),
                ReturnConsumedCapacity = "BOGUS",
            }),
            ValidationSearch);
        var clock = FidelityTime.StartNew();
        List<SearchResultItem> results = [];
        while (clock.Elapsed.TotalSeconds < 90)
        {
            results = (await ddb.SearchVectorsAsync(new SearchVectorsRequest
            {
                TableName = table,
                IndexName = "idx",
                TopK = 1,
                SearchVector = Vec(),
            })).SearchResults ?? [];
            if (results.Count > 0)
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
        }

        Ensure(
            results.Select(r => r.Item["pk"].S).SequenceEqual(["v1"]),
            $"the rejected SearchVectors must leave the index searchable, got {Py.Repr(results.Select(r => r.Item).ToList())}");
    }
}

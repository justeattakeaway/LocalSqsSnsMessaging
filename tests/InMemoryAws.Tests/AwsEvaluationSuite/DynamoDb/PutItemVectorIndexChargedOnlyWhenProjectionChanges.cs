// Translated from behavioral/dynamodb/check_put_item_vector_index_charged_only_when_projection_changes.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.put-item-vector-index-charged-only-when-projection-changes</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutItemVectorIndexChargedOnlyWhenProjectionChanges
{
    private const string CheckId = "put-item-vector-index-charged-only-when-projection-changes";
    private const int Dims = 4;

    private static List<AttributeValue> Vec() => [N("1"), N("0"), N("0"), N("0")];

    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };

    private static VectorIndex Vi(string name, Projection projection) => new()
    {
        IndexName = name,
        VectorAttribute = new VectorAttributeDefinition { AttributeName = "emb" },
        Dimensions = Dims,
        DistanceFunction = "COSINE",
        Projection = projection,
    };

    private static async Task<TableDescription> WaitVectorTableActive(IAmazonDynamoDB ddb, string table, int nIndexes, double timeoutS = 240)
    {
        var clock = FidelityTime.StartNew();
        var tbl = (await ddb.DescribeTableAsync(table)).Table;
        List<VectorIndexDescription> indexes = [];
        while (clock.Elapsed.TotalSeconds < timeoutS)
        {
            tbl = (await ddb.DescribeTableAsync(table)).Table;
            indexes = tbl.VectorIndexes ?? [];
            if (tbl.TableStatus?.ToString() == "ACTIVE" && indexes.Count == nIndexes
                && indexes.All(v => v.IndexStatus?.ToString() == "ACTIVE"))
            {
                return tbl;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail(
            $"table/vector indexes never became ACTIVE: {Py.Repr(tbl.TableStatus)} "
            + $"{Py.Repr(indexes.Select(v => (v.IndexName, v.IndexStatus?.ToString())).ToList())}");
    }

    private static List<string> Sorted(IEnumerable<string> keys) => keys.Order(StringComparer.Ordinal).ToList();

    /// <summary>
    /// PutItem ReturnConsumedCapacity=INDEXES charges a vector index only when the write
    /// changes an attribute that index projects; an identical overwrite charges no vector
    /// index, and ReturnConsumedCapacity=TOTAL carries no VectorIndexes or Table field.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.put-item-vector-index-charged-only-when-projection-changes")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbvecproj-");

        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            VectorIndexes =
            [
                Vi("idx-all", new Projection { ProjectionType = ProjectionType.ALL }),
                Vi("idx-keys", new Projection { ProjectionType = ProjectionType.KEYS_ONLY }),
            ],
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await WaitVectorTableActive(ddb, table, 2);

        // First write: both indexes materialize a fresh entry for the vector attribute.
        var put1 = await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = Item(("pk", S("v1")), ("emb", new AttributeValue { L = Vec() }), ("note", S("first"))),
            ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES,
        });
        var cc1 = put1.ConsumedCapacity?.VectorIndexes ?? [];
        Ensure(
            Sorted(cc1.Keys).SequenceEqual(["idx-all", "idx-keys"]),
            $"a write creating a new vector entry must charge every vector index over the attribute, got {Py.Repr(Sorted(cc1.Keys))}");

        // Second write: same pk, same vector, but 'note' changes. idx-all projects 'note' (ALL);
        // idx-keys does not (KEYS_ONLY).
        var put2 = await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = Item(("pk", S("v1")), ("emb", new AttributeValue { L = Vec() }), ("note", S("second"))),
            ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES,
        });
        var cc2 = put2.ConsumedCapacity?.VectorIndexes ?? [];
        Ensure(
            Sorted(cc2.Keys).SequenceEqual(["idx-all"]),
            $"changing 'note', which only idx-all projects, must charge idx-all and not idx-keys; VectorIndexes was {Py.Repr(Sorted(cc2.Keys))}");

        // Third write: byte-for-byte identical to the second. Nothing projected by either index
        // changed, so neither index is charged.
        var put3 = await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = Item(("pk", S("v1")), ("emb", new AttributeValue { L = Vec() }), ("note", S("second"))),
            ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES,
        });
        var cc3 = put3.ConsumedCapacity;
        Ensure(
            cc3?.VectorIndexes is null,
            $"an identical overwrite must charge no vector index, got {Py.Repr(cc3?.VectorIndexes?.Keys.ToList())}");

        // Fourth write: a real change again, but requested with TOTAL instead of INDEXES: no
        // per-index breakdown and no table breakdown, only the top-level fields.
        var put4 = await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = Item(("pk", S("v1")), ("emb", new AttributeValue { L = Vec() }), ("note", S("third"))),
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        });
        var cc4 = put4.ConsumedCapacity;
        Ensure(cc4?.VectorIndexes is null, $"TOTAL must carry no VectorIndexes field, got {Py.Repr(cc4?.TableName)} {Py.Repr(cc4?.CapacityUnits)}");
        Ensure(cc4?.Table is null, $"TOTAL must carry no Table field, got {Py.Repr(cc4?.TableName)} {Py.Repr(cc4?.CapacityUnits)}");

        var stored = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("v1"))), ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(stored.GetValueOrDefault("note"), S("third")),
            $"GetItem after the fourth write returned note={Py.Repr(stored.GetValueOrDefault("note"))}, expected {{'S': 'third'}}");
    }
}

// Translated from behavioral/dynamodb/check_lsi_non_projected_query_charges_base_item_on_top_of_the_keys_only_index_read.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.lsi-non-projected-query-charges-base-item-on-top-of-the-keys-only-index-read</summary>
[Category("AwsEvaluationSuite")]
public sealed class LsiNonProjectedQueryChargesBaseItemOnTopOfTheKeysOnlyIndexRead
{
    private const string CheckId = "lsi-non-projected-query-charges-base-item-on-top-of-the-keys-only-index-read";
    private static readonly string Payload = new('x', 8000);
    private static readonly string[] KeyAttributes = ["pk", "sk", "lsk"];

    // A KEYS_ONLY LSI entry carries only the base table's key and the index's own sort key.
    private static readonly int IndexOnlyItemBytes = ("pk".Length + "P".Length) + ("sk".Length + "S1".Length) + ("lsk".Length + "L1".Length);
    private static readonly int BaseItemBytes = IndexOnlyItemBytes + "payload".Length + Payload.Length;
    private static readonly int IndexOnlyRcu = (int)Math.Ceiling(IndexOnlyItemBytes / 4096.0);
    private static readonly int BaseRcu = (int)Math.Ceiling(BaseItemBytes / 4096.0);

    private static Dictionary<string, object?>? Cap(Capacity? capacity) =>
        capacity is null ? null : new Dictionary<string, object?> { ["CapacityUnits"] = capacity.CapacityUnits };

    private static Dictionary<string, object?>? Caps(Dictionary<string, Capacity>? capacities) =>
        capacities?.ToDictionary(e => e.Key, e => (object?)Cap(e.Value), StringComparer.Ordinal);

    /// <summary>
    /// A Query on a KEYS_ONLY LSI that names a non-projected attribute fetches the base item and
    /// bills its 4 KB-rounded size in addition to the index-only read; naming a projected attribute
    /// does not.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/LSI.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.lsi-non-projected-query-charges-base-item-on-top-of-the-keys-only-index-read")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-lsisize-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("lsk", ScalarAttributeType.S),
            ],
            LocalSecondaryIndexes =
            [
                new LocalSecondaryIndex
                {
                    IndexName = "lsi1",
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("lsk", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.KEYS_ONLY },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        await ddb.PutItemAsync(table, Item(("pk", S("P")), ("sk", S("S1")), ("lsk", S("L1")), ("payload", S(Payload))));

        Task<QueryResponse> Query(string? projectionExpression) => ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = "lsi1",
            KeyConditionExpression = "pk = :p",
            ProjectionExpression = projectionExpression,
            ExpressionAttributeValues = Item((":p", S("P"))),
            ConsistentRead = true,
            ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES,
        });

        // Positive control: no ProjectionExpression at all stays index-only.
        var bare = await Query(null);
        Ensure(
            bare.Items[0].Keys.ToHashSet(StringComparer.Ordinal).SetEquals(KeyAttributes),
            $"a KEYS_ONLY LSI with no ProjectionExpression must return only the keys, got {Py.Repr(bare.Items[0].Keys.Order(StringComparer.Ordinal).ToList())}");
        var bareCc = bare.ConsumedCapacity;
        Ensure(
            bareCc.CapacityUnits == IndexOnlyRcu,
            $"the index-only read of {Py.Repr(IndexOnlyItemBytes)} raw bytes must bill exactly {Py.Repr(IndexOnlyRcu)} RCU, got {Py.Repr(bareCc.CapacityUnits)}");
        Ensure(bareCc.Table.CapacityUnits == 0, $"an index-only LSI read must charge the Table arm nothing, got {Py.Repr(Cap(bareCc.Table))}");
        Ensure(
            bareCc.LocalSecondaryIndexes["lsi1"].CapacityUnits == IndexOnlyRcu,
            $"the LocalSecondaryIndexes['lsi1'] arm must carry {Py.Repr(IndexOnlyRcu)} RCU, got {Py.Repr(Caps(bareCc.LocalSecondaryIndexes))}");

        // Control: naming a projected (key) attribute still needs no base-table fetch.
        var projected = await Query("sk");
        Ensure(
            Py.Equal(projected.Items, new[] { Item(("sk", S("S1"))) }),
            $"a ProjectionExpression naming only a projected attribute must return exactly that attribute, got {Py.Repr(projected.Items)}");
        var projectedCc = projected.ConsumedCapacity;
        Ensure(
            projectedCc.CapacityUnits == IndexOnlyRcu,
            $"naming a projected attribute must still bill the index-only {Py.Repr(IndexOnlyRcu)} RCU, got {Py.Repr(projectedCc.CapacityUnits)} -- it must not trigger a base-table fetch");
        Ensure(projectedCc.Table.CapacityUnits == 0, $"naming a projected attribute must charge the Table arm nothing, got {Py.Repr(Cap(projectedCc.Table))}");

        // The behaviour under test: naming a NON-projected attribute forces a base-table fetch.
        var fetched = await Query("payload");
        Ensure(
            Py.Equal(fetched.Items, new[] { Item(("payload", S(Payload))) }),
            $"the non-projected attribute must be fetched from the base table and returned, got {Py.Repr(fetched.Items)}");
        var fetchedCc = fetched.ConsumedCapacity;
        Ensure(
            fetchedCc.Table.CapacityUnits == BaseRcu,
            $"the base-table fetch of a {Py.Repr(BaseItemBytes)}-byte item must bill exactly {Py.Repr(BaseRcu)} RCU on the Table arm, got {Py.Repr(Cap(fetchedCc.Table))}");
        Ensure(
            fetchedCc.LocalSecondaryIndexes["lsi1"].CapacityUnits == IndexOnlyRcu,
            $"the LocalSecondaryIndexes['lsi1'] arm must still carry the unchanged index-only {Py.Repr(IndexOnlyRcu)} RCU, got {Py.Repr(Caps(fetchedCc.LocalSecondaryIndexes))}");
        Ensure(
            fetchedCc.CapacityUnits == IndexOnlyRcu + BaseRcu,
            $"the total must be the index-only read PLUS the base item's 4 KB-rounded size: {Py.Repr(IndexOnlyRcu)} + {Py.Repr(BaseRcu)} = {Py.Repr(IndexOnlyRcu + BaseRcu)}, got {Py.Repr(fetchedCc.CapacityUnits)}");
        Ensure(
            fetchedCc.CapacityUnits - bareCc.CapacityUnits == BaseRcu,
            $"the non-projected-attribute Query must exceed the KEYS_ONLY-only read by exactly the base item's 4 KB-rounded size, {Py.Repr(BaseRcu)} RCU, got a difference of {Py.Repr(fetchedCc.CapacityUnits - bareCc.CapacityUnits)}");
    }
}

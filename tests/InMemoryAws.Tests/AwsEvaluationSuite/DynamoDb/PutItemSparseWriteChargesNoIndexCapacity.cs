// Translated from behavioral/dynamodb/check_put_item_sparse_write_charges_no_index_capacity.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.put-item-sparse-write-charges-no-index-capacity</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutItemSparseWriteChargesNoIndexCapacity
{
    /// <summary>The index arms as boto3 hands them to the check: <c>{'gsi1': {'CapacityUnits': 1.0}}</c>.</summary>
    private static Dictionary<string, object?>? Caps(Dictionary<string, Capacity>? arms) =>
        arms?.ToDictionary(a => a.Key, a => (object?)new Dictionary<string, object?> { ["CapacityUnits"] = a.Value.CapacityUnits }, StringComparer.Ordinal);

    /// <summary>
    /// A write that misses both index keys is charged for the table only: no index arm appears,
    /// because nothing was written to an index.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.put-item-sparse-write-charges-no-index-capacity")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("gpk", ScalarAttributeType.S),
                new AttributeDefinition("lsk", ScalarAttributeType.N),
            ],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = "gsi1",
                    KeySchema = [new KeySchemaElement("gpk", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
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
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        var cc = (await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES,
            Item = Item(("pk", S("sparse")), ("sk", S("1"))),
        })).ConsumedCapacity;
        Ensure((cc.Table?.CapacityUnits ?? 0) > 0, "the table write must still be charged");
        Ensure(
            cc.GlobalSecondaryIndexes is null || cc.GlobalSecondaryIndexes.Count == 0,
            $"a write missing the GSI key must charge no GSI arm, got {Py.Repr(Caps(cc.GlobalSecondaryIndexes))}");
        Ensure(
            cc.LocalSecondaryIndexes is null || cc.LocalSecondaryIndexes.Count == 0,
            $"a write missing the LSI key must charge no LSI arm, got {Py.Repr(Caps(cc.LocalSecondaryIndexes))}");
    }
}

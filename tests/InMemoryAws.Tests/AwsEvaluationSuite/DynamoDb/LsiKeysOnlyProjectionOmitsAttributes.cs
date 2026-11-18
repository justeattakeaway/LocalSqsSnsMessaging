// Translated from behavioral/dynamodb/check_lsi_keys_only_projection_omits_attributes.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.lsi-keys-only-projection-omits-attributes</summary>
[Category("AwsEvaluationSuite")]
public sealed class LsiKeysOnlyProjectionOmitsAttributes
{
    private static readonly string[] KeyAttributes = ["pk", "sk", "lsk"];

    /// <summary>
    /// A strongly consistent read of a KEYS_ONLY LSI returns the table keys and the index sort key
    /// and nothing else, so an attribute that exists on the item is absent from the index read.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.lsi-keys-only-projection-omits-attributes")]
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
        await ddb.PutItemAsync(table, Item(("pk", S("a")), ("sk", S("1")), ("gpk", S("g1")), ("lsk", N("5")), ("extra", S("e"))));
        var item = (await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = "lsi1",
            KeyConditionExpression = "pk = :v",
            ExpressionAttributeValues = Item((":v", S("a"))),
            ConsistentRead = true,
        })).Items[0];
        Ensure(item.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(KeyAttributes), $"expected only keys, got {Py.Repr(item.Keys.Order(StringComparer.Ordinal).ToList())}");
        Ensure(!item.ContainsKey("extra"), "a KEYS_ONLY index must not project a non-key attribute");
    }
}

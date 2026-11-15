// Translated from behavioral/dynamodb/check_gsi_query_consumes_half_a_read_unit.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.gsi-query-consumes-half-a-read-unit</summary>
[Category("AwsEvaluationSuite")]
public sealed class GsiQueryConsumesHalfAReadUnit
{
    /// <summary>
    /// A GSI is eventually consistent by construction, so a GSI Query consumes half a read unit --
    /// half what the same read costs strongly consistently on the base table.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.gsi-query-consumes-half-a-read-unit")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("gpk", ScalarAttributeType.S)],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = "gsi1",
                    KeySchema = [new KeySchemaElement("gpk", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        await ddb.PutItemAsync(table, Item(("pk", S("a")), ("gpk", S("g"))));
        var strong = (await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("a"))),
            ConsistentRead = true,
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        })).ConsumedCapacity.CapacityUnits!.Value;
        var idx = (await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = "gsi1",
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
            KeyConditionExpression = "gpk = :v",
            ExpressionAttributeValues = Item((":v", S("g"))),
        })).ConsumedCapacity;
        Ensure(
            idx.CapacityUnits == strong / 2,
            $"a GSI query costs half a strong read: expected {Py.Repr(strong / 2)}, got {Py.Repr(idx.CapacityUnits)}");
    }
}

// Translated from behavioral/dynamodb/check_query_matching_nothing_still_consumes_read_capacity.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-matching-nothing-still-consumes-read-capacity</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryMatchingNothingStillConsumesReadCapacity
{
    /// <summary>
    /// A Query that matches no item still consumes the minimum read capacity for its read type. The
    /// zero-result path is exactly where an engine optimises the meter away.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-matching-nothing-still-consumes-read-capacity")]
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
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        await ddb.PutItemAsync(table, Item(("pk", S("a"))));
        var cc = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
            KeyConditionExpression = "pk = :v",
            ExpressionAttributeValues = Item((":v", S("no-such-key"))),
        });
        Ensure(cc.Count == 0, $"the query must match nothing, got {cc.Count}");
        Ensure(
            cc.ConsumedCapacity.CapacityUnits > 0,
            $"a matchless Query still consumes capacity, got {Py.Repr(new Dictionary<string, object?> { ["TableName"] = cc.ConsumedCapacity.TableName, ["CapacityUnits"] = cc.ConsumedCapacity.CapacityUnits })}");
    }
}

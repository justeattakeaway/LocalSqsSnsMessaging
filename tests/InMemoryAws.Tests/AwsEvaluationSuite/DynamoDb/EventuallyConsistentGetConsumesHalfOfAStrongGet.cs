// Translated from behavioral/dynamodb/check_eventually_consistent_get_consumes_half_of_a_strong_get.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.eventually-consistent-get-consumes-half-of-a-strong-get</summary>
[Category("AwsEvaluationSuite")]
public sealed class EventuallyConsistentGetConsumesHalfOfAStrongGet
{
    /// <summary>
    /// An eventually consistent read consumes half the units of a strongly consistent one, which is
    /// why a table provisioned for N consistent reads serves 2N eventually consistent ones.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.eventually-consistent-get-consumes-half-of-a-strong-get")]
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
        var strong = (await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("a"))),
            ConsistentRead = true,
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        })).ConsumedCapacity.CapacityUnits!.Value;
        var eventual = (await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("a"))),
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        })).ConsumedCapacity.CapacityUnits!.Value;
        Ensure(
            eventual == strong / 2,
            $"an eventually consistent read is half a strong one: expected {Py.Repr(strong / 2)}, got {Py.Repr(eventual)}");
    }
}

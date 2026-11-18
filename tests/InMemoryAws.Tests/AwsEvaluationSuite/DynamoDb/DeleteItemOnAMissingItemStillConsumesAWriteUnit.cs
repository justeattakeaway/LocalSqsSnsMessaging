// Translated from behavioral/dynamodb/check_delete_item_on_a_missing_item_still_consumes_a_write_unit.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.delete-item-on-a-missing-item-still-consumes-a-write-unit</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteItemOnAMissingItemStillConsumesAWriteUnit
{
    /// <summary>
    /// A DeleteItem against a key that was never written is a no-op on state but NOT free: it still
    /// consumes a write unit. An engine that short-circuits the delete reports zero and diverges.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.delete-item-on-a-missing-item-still-consumes-a-write-unit")]
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
        var cc = (await ddb.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("ghost"))),
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        })).ConsumedCapacity;
        Ensure(cc.CapacityUnits == 1.0, $"deleting a non-existent item still costs 1 write unit, got {Py.Repr(cc.CapacityUnits)}");
        Ensure((await ddb.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = true })).Count == 0, "the table must stay empty");
    }
}

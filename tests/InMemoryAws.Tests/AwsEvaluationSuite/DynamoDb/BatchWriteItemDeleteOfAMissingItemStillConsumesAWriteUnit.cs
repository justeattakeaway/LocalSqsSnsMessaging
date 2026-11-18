// Translated from behavioral/dynamodb/check_batch_write_item_delete_of_a_missing_item_still_consumes_a_write_unit.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-write-item-delete-of-a-missing-item-still-consumes-a-write-unit</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchWriteItemDeleteOfAMissingItemStillConsumesAWriteUnit
{
    /// <summary>
    /// The same rule inside a batch: a DeleteRequest for a key that does not exist is billed a write
    /// unit, and the batch reports it per table.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.batch-write-item-delete-of-a-missing-item-still-consumes-a-write-unit")]
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
        var cc = (await ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new() { [table] = [new WriteRequest { DeleteRequest = new DeleteRequest { Key = Item(("pk", S("ghost"))) } }] },
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        })).ConsumedCapacity;
        Ensure(cc.Count == 1, $"expected one per-table capacity entry, got {Py.Repr(cc.Select(c => c.CapacityUnits))}");
        Ensure(
            cc[0].CapacityUnits == 1.0,
            $"a batched delete of a missing item still costs 1 unit, got {Py.Repr(cc[0].CapacityUnits)}");
    }
}

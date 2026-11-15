// Translated from behavioral/dynamodb/check_delete_item_write_capacity_billed_on_deleted_item_size.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.delete-item-write-capacity-billed-on-deleted-item-size</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteItemWriteCapacityBilledOnDeletedItemSize
{
    private const string Check = "delete-item-write-capacity-billed-on-deleted-item-size";

    /// <summary>"pk"(2)+pk(3)+"v"(1)+raw_len bytes of string value.</summary>
    private static Dictionary<string, AttributeValue> ItemOf(string pk, int rawLen) =>
        Item(("pk", S(pk)), ("v", S(new string('x', rawLen))));

    /// <summary>
    /// DeleteItem's write capacity is billed on the size of the item it deletes: a 1024-byte item
    /// costs 1 write capacity unit and a 1025-byte item costs 2, not a flat per-call charge.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.delete-item-write-capacity-billed-on-deleted-item-size")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-delsz-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        var atBoundary = ItemOf("k01", 1018); // "pk"(2)+"k01"(3)+"v"(1)+1018 = 1024 bytes
        await ddb.PutItemAsync(table, atBoundary);
        var ccAt = (await ddb.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("k01"))),
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        })).ConsumedCapacity;
        Ensure(ccAt.CapacityUnits == 1.0, $"deleting a 1024-byte item must cost 1.0 write capacity unit, got {Py.Repr(ccAt.CapacityUnits)}");
        Ensure(
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("k01"))), ConsistentRead = true })).Item is null,
            "the deleted 1024-byte item must be gone");

        var overBoundary = ItemOf("k02", 1019); // one byte over the boundary -> 1025 bytes
        await ddb.PutItemAsync(table, overBoundary);
        var ccOver = (await ddb.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("k02"))),
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        })).ConsumedCapacity;
        Ensure(ccOver.CapacityUnits == 2.0, $"deleting a 1025-byte item must cost 2.0 write capacity units, got {Py.Repr(ccOver.CapacityUnits)}");
        Ensure(
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("k02"))), ConsistentRead = true })).Item is null,
            "the deleted 1025-byte item must be gone");
    }
}

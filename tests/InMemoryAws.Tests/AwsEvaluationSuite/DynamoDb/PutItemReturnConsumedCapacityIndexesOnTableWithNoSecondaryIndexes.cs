// Translated from behavioral/dynamodb/check_put_item_return_consumed_capacity_indexes_on_table_with_no_secondary_indexes.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.put-item-return-consumed-capacity-indexes-on-table-with-no-secondary-indexes</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutItemReturnConsumedCapacityIndexesOnTableWithNoSecondaryIndexes
{
    private const string Check = "put-item-return-consumed-capacity-indexes-on-table-with-no-secondary-indexes";

    /// <summary>
    /// On a table with no secondary index, PutItem's ReturnConsumedCapacity=INDEXES reports only
    /// the Table arm, with no GlobalSecondaryIndexes or LocalSecondaryIndexes map at all.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.put-item-return-consumed-capacity-indexes-on-table-with-no-secondary-indexes")]
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
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        var key = Item(("pk", S("a")), ("sk", S("1")));
        var cc = (await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = Item(("pk", S("a")), ("sk", S("1")), ("attr1", S("one"))),
            ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES,
        })).ConsumedCapacity;

        Ensure(
            cc.Table?.CapacityUnits == 1.0,
            $"the table arm's CapacityUnits was {Py.Repr(cc.Table?.CapacityUnits)}, expected 1.0");
        Ensure(
            cc.GlobalSecondaryIndexes is null,
            $"a table with no GSI reported a GlobalSecondaryIndexes arm: {Py.Repr(cc.GlobalSecondaryIndexes?.Keys)}");
        Ensure(
            cc.LocalSecondaryIndexes is null,
            $"a table with no LSI reported a LocalSecondaryIndexes arm: {Py.Repr(cc.LocalSecondaryIndexes?.Keys)}");
        Ensure(
            cc.CapacityUnits == cc.Table!.CapacityUnits,
            $"the total {Py.Repr(cc.CapacityUnits)} must equal the table arm {Py.Repr(cc.Table.CapacityUnits)} " +
            "when no index arm is present");

        var readBack = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(readBack?.GetValueOrDefault("attr1")?.S == "one", $"the write did not persist: {Py.Repr(readBack)}");
    }
}

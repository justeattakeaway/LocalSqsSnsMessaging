// Translated from behavioral/dynamodb/check_transact_write_items_consumes_twice_a_plain_write.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-items-consumes-twice-a-plain-write</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteItemsConsumesTwiceAPlainWrite
{
    /// <summary>
    /// A transactional write costs two write units per KB -- one to prepare, one to commit -- where
    /// PutItem costs one. Asserted as a ratio on a table with NO index, so no index arm confounds it.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-items-consumes-twice-a-plain-write")]
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
        var plain = (await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = Item(("pk", S("a"))),
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        })).ConsumedCapacity.CapacityUnits!.Value;
        var tx = (await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems = [new TransactWriteItem { Put = new Put { TableName = table, Item = Item(("pk", S("b"))) } }],
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        })).ConsumedCapacity[0];
        Ensure(plain == 1.0, $"a PutItem of a tiny item costs 1 write unit, got {Py.Repr(plain)}");
        Ensure(
            tx.CapacityUnits == 2 * plain,
            $"a transactional write costs twice a plain write: expected {Py.Repr(2 * plain)}, got {Py.Repr(tx.CapacityUnits)}");
        Ensure(
            tx.WriteCapacityUnits == tx.CapacityUnits,
            $"TransactWriteItems reports the write arm beside the total, got TableName={tx.TableName} CapacityUnits={Py.Repr(tx.CapacityUnits)} WriteCapacityUnits={Py.Repr(tx.WriteCapacityUnits)}");
    }
}

// Translated from behavioral/dynamodb/check_transact_get_items_consumes_twice_a_strongly_consistent_read.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-get-items-consumes-twice-a-strongly-consistent-read</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactGetItemsConsumesTwiceAStronglyConsistentRead
{
    /// <summary>
    /// A transactional read costs two read units where a strongly consistent GetItem costs one, so
    /// the multiplier is asserted as a RATIO against the same item rather than as a bare number.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-get-items-consumes-twice-a-strongly-consistent-read")]
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
        var plain = (await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("a"))),
            ConsistentRead = true,
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        })).ConsumedCapacity.CapacityUnits!.Value;
        var tx = (await ddb.TransactGetItemsAsync(new TransactGetItemsRequest
        {
            TransactItems = [new TransactGetItem { Get = new Get { TableName = table, Key = Item(("pk", S("a"))) } }],
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        })).ConsumedCapacity[0];
        Ensure(plain == 1.0, $"a strongly consistent GetItem of a tiny item costs 1 unit, got {Py.Repr(plain)}");
        Ensure(
            tx.CapacityUnits == 2 * plain,
            $"a transactional read costs twice a strong read: expected {Py.Repr(2 * plain)}, got {Py.Repr(tx.CapacityUnits)}");
        Ensure(
            tx.ReadCapacityUnits == tx.CapacityUnits,
            $"TransactGetItems reports the read arm beside the total, got TableName={tx.TableName} CapacityUnits={Py.Repr(tx.CapacityUnits)} ReadCapacityUnits={Py.Repr(tx.ReadCapacityUnits)}");
    }
}

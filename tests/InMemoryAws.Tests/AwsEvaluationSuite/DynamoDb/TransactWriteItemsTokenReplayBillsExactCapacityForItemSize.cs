// Translated from behavioral/dynamodb/check_transact_write_items_token_replay_bills_exact_capacity_for_item_size.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-items-token-replay-bills-exact-capacity-for-item-size</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteItemsTokenReplayBillsExactCapacityForItemSize
{
    private const string Check = "transact-write-items-token-replay-bills-exact-capacity-for-item-size";
    private const string PkName = "pk";
    private const string ValName = "payload";
    private const int TargetBytes = 1500; // within (1024, 2048]: 2 base write units; within (0, 4096]: 1 base read unit

    private static Dictionary<string, AttributeValue> MakeItem(string pkValue)
    {
        var overhead = Encoding.UTF8.GetByteCount(PkName) + Encoding.UTF8.GetByteCount(pkValue) + Encoding.UTF8.GetByteCount(ValName);
        var padLen = TargetBytes - overhead;
        return Item((PkName, S(pkValue)), (ValName, S(new string('x', padLen))));
    }

    /// <summary>
    /// A ~1.5KB TransactWriteItems Put bills 4 write capacity units; a same-token replay of it
    /// bills 2 read capacity units, a transactional read of the stored item, and no write capacity.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-items-token-replay-bills-exact-capacity-for-item-size")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement(PkName, KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition(PkName, ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        var item = MakeItem("txn-item");

        async Task<ConsumedCapacity> PutTx(string clientToken) =>
            (await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                ClientRequestToken = clientToken,
                ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES,
                TransactItems =
                [
                    new TransactWriteItem
                    {
                        Put = new Put
                        {
                            TableName = table,
                            Item = item,
                            ConditionExpression = $"attribute_not_exists({PkName})",
                        },
                    },
                ],
            })).ConsumedCapacity[0];

        async Task<Dictionary<string, AttributeValue>> Stored() =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item((PkName, item[PkName])), ConsistentRead = true })).Item;

        var token = Guid.NewGuid().ToString();

        // --- first use: a write of the committed item ---
        var first = await PutTx(token);
        Ensure(
            first.WriteCapacityUnits == 4.0,
            $"a ~1.5KB TransactWriteItems Put billed WriteCapacityUnits {Py.Repr(first.WriteCapacityUnits)}, expected 4.0");
        Ensure(
            (first.ReadCapacityUnits ?? 0) == 0,
            $"the first use of a token billed ReadCapacityUnits {Py.Repr(first.ReadCapacityUnits)}, expected none");
        Ensure(first.CapacityUnits == 4.0, $"total CapacityUnits was {Py.Repr(first.CapacityUnits)}, expected 4.0");
        Ensure(Py.Equal(await Stored(), item), $"the committed item is {Py.Repr(await Stored())}, expected {Py.Repr(item)}");

        // --- same-token replay: a read of the same item, not a re-execution of the write ---
        var replay = await PutTx(token);
        Ensure(
            replay.ReadCapacityUnits == 2.0,
            $"a same-token replay of a ~1.5KB TransactWriteItems billed ReadCapacityUnits {Py.Repr(replay.ReadCapacityUnits)}, expected 2.0");
        Ensure(
            (replay.WriteCapacityUnits ?? 0) == 0,
            $"the replay billed WriteCapacityUnits {Py.Repr(replay.WriteCapacityUnits)}, expected none");
        Ensure(replay.CapacityUnits == 2.0, $"total CapacityUnits was {Py.Repr(replay.CapacityUnits)}, expected 2.0");
        Ensure(
            Py.Equal(await Stored(), item),
            $"the item changed to {Py.Repr(await Stored())} after a replay; a deduplicated replay must leave it untouched");

        // --- positive control: a FRESH token against a same-size item is a write again ---
        var freshItem = MakeItem("txn-item-fresh");
        var fresh = (await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            ClientRequestToken = Guid.NewGuid().ToString(),
            ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES,
            TransactItems = [new TransactWriteItem { Put = new Put { TableName = table, Item = freshItem } }],
        })).ConsumedCapacity[0];
        Ensure(
            fresh.WriteCapacityUnits == 4.0,
            $"a fresh token billed WriteCapacityUnits {Py.Repr(fresh.WriteCapacityUnits)}, expected 4.0");
        Ensure(
            (fresh.ReadCapacityUnits ?? 0) == 0,
            $"a fresh token billed ReadCapacityUnits {Py.Repr(fresh.ReadCapacityUnits)}, expected none");
        var freshGot = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item((PkName, freshItem[PkName])), ConsistentRead = true })).Item;
        Ensure(Py.Equal(freshGot, freshItem), "the fresh-token item was not committed as written");
    }
}

// Translated from behavioral/dynamodb/check_transact_write_items_update_shares_update_items_floor_bound_ceiling_at_a_short_key.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-items-update-shares-update-items-floor-bound-ceiling-at-a-short-key</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteItemsUpdateSharesUpdateItemsFloorBoundCeilingAtAShortKey
{
    private const string Check = "transact-write-items-update-shares-update-items-floor-bound-ceiling-at-a-short-key";
    private const int Cap = 409600;
    private static readonly (string Code, string Message) ItemTooLarge = ("ValidationException", "Item size to update has exceeded the maximum allowed size");

    /// <summary>
    /// TransactWriteItems' Update action, on a one-byte key, is bound by the same floor-bound
    /// ceiling as a standalone UpdateItem, not the flat 409600-byte item cap, and a statement
    /// exceeding it raises the identical ValidationException code and message.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ServiceQuotas.html#limits-items
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-items-update-shares-update-items-floor-bound-ceiling-at-a-short-key")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbtxnupd-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("k", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("k", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 30);

        async Task<Dictionary<string, AttributeValue>> Get(Dictionary<string, AttributeValue> key) =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;

        Task<UpdateItemResponse> UpdateItem(Dictionary<string, AttributeValue> key, string value) => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET v = :v",
            ExpressionAttributeValues = Item((":v", S(value))),
        });

        Task<TransactWriteItemsResponse> TransactUpdate(Dictionary<string, AttributeValue> key, string value) =>
            ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                TransactItems =
                [
                    new TransactWriteItem
                    {
                        Update = new Update
                        {
                            TableName = table,
                            Key = key,
                            UpdateExpression = "SET v = :v",
                            ExpressionAttributeValues = Item((":v", S(value))),
                        },
                    },
                ],
            });

        // Every key below is one byte, far under the 22-byte floor that keeps UpdateItem's own
        // ceiling flat and short of the flat 409600-byte item cap.
        var controlKey = Item(("k", S("0")));
        await ddb.PutItemAsync(table, Item(("k", S("0")), ("v", S("start"))));

        // Positive control: a standalone UpdateItem's own ceiling for this short key tops out 20
        // bytes below the flat cap (409600 minus the 22-byte floor, plus the 2 preexisting key
        // bytes); at that ceiling it succeeds.
        const int ceilingTotal = Cap - 20;
        var ceilingValue = new string('x', ceilingTotal - ItemBytes(controlKey, ""));
        await UpdateItem(controlKey, ceilingValue);
        var controlGot = await Get(controlKey);
        Ensure(
            ItemBytes(controlKey, controlGot["v"].S) == ceilingTotal,
            $"UpdateItem's own ceiling write landed at {ItemBytes(controlKey, controlGot["v"].S)} bytes, expected {ceilingTotal}");

        // Refusal: targeting the flat 409600-byte cap on this short key is 20 bytes past
        // UpdateItem's own ceiling, and UpdateItem refuses it -- the item stays at its seeded value.
        var refuseKey = Item(("k", S("1")));
        await ddb.PutItemAsync(table, Item(("k", S("1")), ("v", S("start"))));
        var overCeilingValue = new string('x', Cap - ItemBytes(refuseKey, ""));
        await ExpectError(() => UpdateItem(refuseKey, overCeilingValue), ItemTooLarge.Code, ItemTooLarge.Message);
        var refused = await Get(refuseKey);
        Ensure(refused["v"].S == "start", $"a refused UpdateItem must leave the item unchanged, got {Py.Repr(refused["v"].S)}");

        // TransactWriteItems' Update, on the same short-key shape, accepts exactly the same
        // ceiling a standalone UpdateItem accepts -- it does not get the flat 409600-byte cap
        // UpdateItem is denied here.
        var txnKey = Item(("k", S("2")));
        await ddb.PutItemAsync(table, Item(("k", S("2")), ("v", S("start"))));
        var txnCeilingValue = new string('x', ceilingTotal - ItemBytes(txnKey, ""));
        Ensure(txnCeilingValue.Length == ceilingValue.Length, "txn and UpdateItem targeted different totals");
        await TransactUpdate(txnKey, txnCeilingValue);
        var txnGot = await Get(txnKey);
        Ensure(
            txnGot["v"].S == txnCeilingValue,
            $"the {ceilingTotal}-byte value must round-trip whole through TransactWriteItems Update, got {txnGot["v"].S.Length} bytes back");
        Ensure(
            ItemBytes(txnKey, txnGot["v"].S) == ceilingTotal,
            $"stored item size is {ItemBytes(txnKey, txnGot["v"].S)}, expected {ceilingTotal}");

        // And TransactWriteItems' Update refuses the same statement UpdateItem refuses, with the
        // identical ValidationException code and message -- the call itself fails, not a
        // TransactionCanceledException wrapping a per-item cancellation reason.
        var overKey = Item(("k", S("3")));
        await ddb.PutItemAsync(table, Item(("k", S("3")), ("v", S("start"))));
        var txnOverCeilingValue = new string('x', Cap - ItemBytes(overKey, ""));
        Ensure(txnOverCeilingValue.Length == overCeilingValue.Length, "txn and UpdateItem targeted different over-ceiling totals");
        await ExpectError(() => TransactUpdate(overKey, txnOverCeilingValue), ItemTooLarge.Code, ItemTooLarge.Message);
        var overUnchanged = await Get(overKey);
        Ensure(
            overUnchanged["v"].S == "start",
            $"a TransactWriteItems Update refused for size must leave the item unchanged, got {Py.Repr(overUnchanged["v"].S)}");
    }

    /// <summary>Encoded size of {key, 'v': {'S': data_value}}: every attribute name's and value's bytes.</summary>
    private static int ItemBytes(Dictionary<string, AttributeValue> key, string dataValue) =>
        "v".Length + dataValue.Length + key.Sum(e => e.Key.Length + e.Value.S.Length);

    private static async Task ExpectError(Func<Task> fn, string code, string message)
    {
        var error = await FidelityContext.CaptureError(fn) ?? throw Fail("expected the call to be refused, but it succeeded");
        Ensure(error.Code == code, $"expected {Py.Repr(code)}, got {Py.Repr(error.Code)} ({Py.Repr(error.Message)})");
        Ensure(error.Message == message, $"unexpected message {Py.Repr(error.Message)}");
    }
}

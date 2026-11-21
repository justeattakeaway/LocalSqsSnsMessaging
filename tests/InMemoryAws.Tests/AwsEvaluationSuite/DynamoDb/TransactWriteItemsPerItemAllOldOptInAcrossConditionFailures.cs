// Translated from behavioral/dynamodb/check_transact_write_items_per_item_all_old_opt_in_across_condition_failures.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-items-per-item-all-old-opt-in-across-condition-failures</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteItemsPerItemAllOldOptInAcrossConditionFailures
{
    private const string Check = "transact-write-items-per-item-all-old-opt-in-across-condition-failures";
    private const string ErrTransactionCanceled = "TransactionCanceledException";
    private const string ConditionFailedMessage = "The conditional request failed";
    private const string CanceledPrefix = "Transaction cancelled, please refer cancellation reasons for specific reasons ";

    /// <summary>
    /// Four condition-check failures in one TransactWriteItems each carry a reason positionally
    /// aligned with its TransactItems slot, with Item present only for the slots that set
    /// ReturnValuesOnConditionCheckFailure=ALL_OLD, independently of whether their neighbors opted in.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_TransactWriteItems.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-items-per-item-all-old-opt-in-across-condition-failures")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        var itemA = Item(("pk", S("A")), ("ver", N("1")), ("tag", S("alpha")));
        var itemB = Item(("pk", S("B")), ("ver", N("1")), ("tag", S("bravo")));
        var itemC = Item(("pk", S("C")), ("ver", N("1")), ("tag", S("charlie")));
        var itemD = Item(("pk", S("D")), ("ver", N("1")), ("tag", S("delta")));
        foreach (var item in new[] { itemA, itemB, itemC, itemD })
        {
            await ddb.PutItemAsync(t, item);
        }

        // Slot 0 would succeed; slots 1-4 all fail their ConditionExpression, with ALL_OLD opted in on
        // slots 1 and 3 only (a ConditionCheck, an Update, a Delete and a Put, one action type each).
        var err = await FidelityContext.CaptureError(() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem { Put = new Put { TableName = t, Item = Item(("pk", S("E")), ("tag", S("echo"))) } },
                new TransactWriteItem
                {
                    ConditionCheck = new ConditionCheck
                    {
                        TableName = t,
                        Key = Item(("pk", S("A"))),
                        ConditionExpression = "ver = :stale",
                        ExpressionAttributeValues = Item((":stale", N("99"))),
                        ReturnValuesOnConditionCheckFailure = ReturnValuesOnConditionCheckFailure.ALL_OLD,
                    },
                },
                new TransactWriteItem
                {
                    Update = new Update
                    {
                        TableName = t,
                        Key = Item(("pk", S("B"))),
                        UpdateExpression = "SET tag = :new",
                        ConditionExpression = "ver = :stale",
                        ExpressionAttributeValues = Item((":new", S("clobbered")), (":stale", N("99"))),
                    },
                },
                new TransactWriteItem
                {
                    Delete = new Delete
                    {
                        TableName = t,
                        Key = Item(("pk", S("C"))),
                        ConditionExpression = "ver = :stale",
                        ExpressionAttributeValues = Item((":stale", N("99"))),
                        ReturnValuesOnConditionCheckFailure = ReturnValuesOnConditionCheckFailure.ALL_OLD,
                    },
                },
                new TransactWriteItem
                {
                    Put = new Put
                    {
                        TableName = t,
                        Item = Item(("pk", S("D")), ("ver", N("2")), ("tag", S("clobbered"))),
                        ConditionExpression = "ver = :stale",
                        ExpressionAttributeValues = Item((":stale", N("99"))),
                    },
                },
            ],
        }));

        Ensure(err is not null, "four stale conditions must cancel the transaction");
        Ensure(err.Code == ErrTransactionCanceled, $"expected {ErrTransactionCanceled}, got {err.Code}");
        var expectedMessage = CanceledPrefix
            + "[None, ConditionalCheckFailed, ConditionalCheckFailed, ConditionalCheckFailed, ConditionalCheckFailed]";
        Ensure(err.Message == expectedMessage, $"expected message {Py.Repr(expectedMessage)}, got {Py.Repr(err.Message)}");

        var reasons = err.CancellationReasons();
        Ensure(
            reasons is not null && reasons.Count == 5,
            $"CancellationReasons must be positionally aligned with the 5 TransactItems, got {Py.Repr(reasons?.Select(r => r.Code))}");

        var ok = reasons![0];
        Ensure(ok.Code == "None", $"slot 0 (not opted into ALL_OLD, not failing) must be \"None\", got {Py.Repr(ok.Code)}");
        Ensure(ok.Item is null, $"slot 0 must carry no Item, got {Py.Repr(ok.Item)}");
        Ensure(ok.Message is null, $"slot 0 must carry no Message, got {Py.Repr(ok.Message)}");

        foreach (var (slot, expectedItem) in new[] { (1, itemA), (3, itemC) })
        {
            var reason = reasons[slot];
            Ensure(reason.Code == "ConditionalCheckFailed", $"slot {slot} must report ConditionalCheckFailed, got {Py.Repr(reason.Code)}");
            Ensure(
                reason.Message == ConditionFailedMessage,
                $"slot {slot} must carry {Py.Repr(ConditionFailedMessage)}, got {Py.Repr(reason.Message)}");
            Ensure(
                Py.Equal(reason.Item, expectedItem),
                $"slot {slot} opted into ALL_OLD and must return {Py.Repr(expectedItem)}, got {Py.Repr(reason.Item)}");
        }

        foreach (var slot in new[] { 2, 4 })
        {
            var reason = reasons[slot];
            Ensure(reason.Code == "ConditionalCheckFailed", $"slot {slot} must report ConditionalCheckFailed, got {Py.Repr(reason.Code)}");
            Ensure(
                reason.Message == ConditionFailedMessage,
                $"slot {slot} must carry {Py.Repr(ConditionFailedMessage)}, got {Py.Repr(reason.Message)}");
            Ensure(reason.Item is null, $"slot {slot} did not opt into ALL_OLD and must carry no Item, got {Py.Repr(reason.Item)}");
        }

        // Effects absent: nothing the canceled transaction touched actually changed.
        Ensure(await Get(ddb, t, "E") is null, "the transaction was canceled, so slot 0 must not have created item E");
        foreach (var expectedItem in new[] { itemA, itemB, itemC, itemD })
        {
            var still = await Get(ddb, t, expectedItem["pk"].S);
            Ensure(
                Py.Equal(still, expectedItem),
                $"{Py.Repr(expectedItem["pk"])} must be unchanged by the canceled transaction, got {Py.Repr(still)}");
        }

        // Positive control: the same item types, each condition now satisfied, commit together.
        await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem { Put = new Put { TableName = t, Item = Item(("pk", S("E")), ("tag", S("echo"))) } },
                new TransactWriteItem
                {
                    ConditionCheck = new ConditionCheck
                    {
                        TableName = t,
                        Key = Item(("pk", S("A"))),
                        ConditionExpression = "ver = :cur",
                        ExpressionAttributeValues = Item((":cur", N("1"))),
                    },
                },
                new TransactWriteItem
                {
                    Update = new Update
                    {
                        TableName = t,
                        Key = Item(("pk", S("B"))),
                        UpdateExpression = "SET tag = :new",
                        ConditionExpression = "ver = :cur",
                        ExpressionAttributeValues = Item((":new", S("updated")), (":cur", N("1"))),
                    },
                },
                new TransactWriteItem
                {
                    Delete = new Delete
                    {
                        TableName = t,
                        Key = Item(("pk", S("C"))),
                        ConditionExpression = "ver = :cur",
                        ExpressionAttributeValues = Item((":cur", N("1"))),
                    },
                },
                new TransactWriteItem
                {
                    Put = new Put
                    {
                        TableName = t,
                        Item = Item(("pk", S("D")), ("ver", N("2")), ("tag", S("updated"))),
                        ConditionExpression = "ver = :cur",
                        ExpressionAttributeValues = Item((":cur", N("1"))),
                    },
                },
            ],
        });

        var eAfter = await Get(ddb, t, "E");
        Ensure(Py.Equal(eAfter, Item(("pk", S("E")), ("tag", S("echo")))), $"slot 0 must have committed, got {Py.Repr(eAfter)}");
        var aAfter = await Get(ddb, t, "A");
        Ensure(Py.Equal(aAfter, itemA), $"a passing ConditionCheck must leave A unchanged, got {Py.Repr(aAfter)}");
        var bAfter = await Get(ddb, t, "B");
        Ensure(
            Py.Equal(bAfter, Item(("pk", S("B")), ("ver", N("1")), ("tag", S("updated")))),
            $"the Update must have committed, got {Py.Repr(bAfter)}");
        Ensure(await Get(ddb, t, "C") is null, "the Delete must have committed");
        var dAfter = await Get(ddb, t, "D");
        Ensure(
            Py.Equal(dAfter, Item(("pk", S("D")), ("ver", N("2")), ("tag", S("updated")))),
            $"the Put must have committed, got {Py.Repr(dAfter)}");
    }

    private static async Task<Dictionary<string, AttributeValue>?> Get(IAmazonDynamoDB ddb, string table, string pk) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;
}

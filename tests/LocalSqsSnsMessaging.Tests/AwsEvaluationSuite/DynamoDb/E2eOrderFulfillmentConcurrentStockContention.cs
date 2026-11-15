// Translated from behavioral/dynamodb/check_e2e_order_fulfillment_concurrent_stock_contention.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.e2e-order-fulfillment-concurrent-stock-contention</summary>
[Category("AwsEvaluationSuite")]
public sealed class E2eOrderFulfillmentConcurrentStockContention
{
    private static readonly string[] OrderKeys = ["ORDER#A", "ORDER#B"];

    private static readonly Dictionary<string, string> ReservationKeys = new(StringComparer.Ordinal)
    {
        ["ORDER#A"] = "RESERVATION#A",
        ["ORDER#B"] = "RESERVATION#B",
    };

    private static readonly string[][] AllowedReasons =
    [
        ["ConditionalCheckFailed", "None", "None"],
        ["TransactionConflict", "None", "None"],
    ];

    /// <summary>
    /// Two parallel conditional TransactWriteItems settlements against one unit of stock leave
    /// exactly one order FULFILLED and stock at 0; the loser gets TransactionCanceledException and
    /// its order stays RESERVED with its reservation HELD (all-or-nothing under contention).
    /// </summary>
    [Test]
    [DisplayName("dynamodb.e2e-order-fulfillment-concurrent-stock-contention")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var tableName = FidelityContext.UniqueName("sim2real-behav-fulfill-race-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = "e2e-order-fulfillment-concurrent-stock-contention" }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(tableName));
        await ddb.WaitForTableExists(tableName, delaySeconds: 2, maxAttempts: 250);

        const string stockKey = "STOCK#sku-scarce";

        await ddb.PutItemAsync(tableName, Item(("pk", S(stockKey)), ("quantity", N("1"))));
        foreach (var orderKey in OrderKeys)
        {
            await ddb.PutItemAsync(tableName, Item(("pk", S(orderKey)), ("status", S("PENDING")), ("sku", S(stockKey))));
            var reservationKey = ReservationKeys[orderKey];
            await ddb.PutItemAsync(new PutItemRequest
            {
                TableName = tableName,
                Item = Item(("pk", S(reservationKey)), ("order", S(orderKey)), ("qty", N("1")), ("status", S("HELD"))),
                ConditionExpression = "attribute_not_exists(pk)",
            });
            await ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = tableName,
                Key = Item(("pk", S(orderKey))),
                UpdateExpression = "SET #s = :new_status",
                ConditionExpression = "#s = :expected_status",
                ExpressionAttributeNames = Names(("#s", "status")),
                ExpressionAttributeValues = Item((":new_status", S("RESERVED")), (":expected_status", S("PENDING"))),
            });
        }

        async Task<(string OrderKey, AwsError? Error)> Settle(string orderKey)
        {
            var reservationKey = ReservationKeys[orderKey];
            var error = await FidelityContext.CaptureError(() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                TransactItems =
                [
                    new TransactWriteItem
                    {
                        Update = new Update
                        {
                            TableName = tableName,
                            Key = Item(("pk", S(stockKey))),
                            UpdateExpression = "SET quantity = quantity - :qty",
                            ConditionExpression = "quantity >= :qty",
                            ExpressionAttributeValues = Item((":qty", N("1"))),
                        },
                    },
                    new TransactWriteItem
                    {
                        Update = new Update
                        {
                            TableName = tableName,
                            Key = Item(("pk", S(orderKey))),
                            UpdateExpression = "SET #s = :new_status",
                            ConditionExpression = "#s = :expected_status",
                            ExpressionAttributeNames = Names(("#s", "status")),
                            ExpressionAttributeValues = Item((":new_status", S("FULFILLED")), (":expected_status", S("RESERVED"))),
                        },
                    },
                    new TransactWriteItem
                    {
                        Delete = new Delete
                        {
                            TableName = tableName,
                            Key = Item(("pk", S(reservationKey))),
                            ConditionExpression = "attribute_exists(pk)",
                        },
                    },
                ],
            }));
            return (orderKey, error);
        }

        (string OrderKey, AwsError? Error)[] results;
        using (ctx.Concurrently())
        {
            results = await Task.WhenAll(OrderKeys.Select(k => Task.Run(() => Settle(k), cancellationToken)));
        }

        var winners = results.Where(r => r.Error is null).Select(r => r.OrderKey).ToList();
        var losers = results.Where(r => r.Error is not null).ToDictionary(r => r.OrderKey, r => r.Error!, StringComparer.Ordinal);

        Ensure(winners.Count == 1, $"expected exactly one winner, got {Py.Repr(winners)} (results={Py.Repr(results.Select(r => (r.OrderKey, r.Error?.ToString())).ToList())})");
        Ensure(losers.Count == 1, $"expected exactly one loser, got {Py.Repr(losers.Keys.ToList())}");
        var (loserKey, loserError) = losers.First();
        var loserCode = loserError.Code;
        var loserMessage = loserError.Message;
        Ensure(loserCode == "TransactionCanceledException", $"expected TransactionCanceledException for the loser, saw {Py.Repr(loserCode)}");
        // The loser lost either on the stock condition (ConditionalCheckFailed) or by overlapping the
        // winner (TransactionConflict), on the stock update, the transaction's first action; the other
        // two actions carry no reason. The message lists those reasons in action order.
        var reasons = (loserError.CancellationReasons() ?? []).Select(r => r.Code).ToList();
        Ensure(
            AllowedReasons.Any(allowed => reasons.SequenceEqual(allowed)),
            $"expected the loser cancelled on the stock update alone, got reasons {Py.Repr(reasons)}");
        var expectedMessage =
            "Transaction cancelled, please refer cancellation reasons for specific reasons "
            + $"[{string.Join(", ", reasons)}]";
        Ensure(loserMessage == expectedMessage, $"expected the loser's message {Py.Repr(expectedMessage)}, got {Py.Repr(loserMessage)}");

        var stockAfter = await ddb.GetItemAsync(new GetItemRequest { TableName = tableName, Key = Item(("pk", S(stockKey))), ConsistentRead = true });
        Ensure(
            stockAfter.Item["quantity"].N == "0",
            $"expected stock to be depleted to 0 after exactly one settlement, got {Py.Repr(stockAfter.Item["quantity"].N)}");

        var winnerKey = winners[0];
        var winnerOrder = await ddb.GetItemAsync(new GetItemRequest { TableName = tableName, Key = Item(("pk", S(winnerKey))), ConsistentRead = true });
        Ensure(
            winnerOrder.Item["status"].S == "FULFILLED",
            $"expected winner order {Py.Repr(winnerKey)} to be FULFILLED, got {Py.Repr(winnerOrder.Item["status"].S)}");
        var winnerReservation = await ddb.GetItemAsync(new GetItemRequest { TableName = tableName, Key = Item(("pk", S(ReservationKeys[winnerKey]))), ConsistentRead = true });
        Ensure(winnerReservation.Item is null, "winner's reservation hold should have been released");

        // The loser's TransactWriteItems was rejected wholesale: its order status and reservation
        // hold must be exactly as they were before the race (untouched by the failed transaction).
        var loserOrder = await ddb.GetItemAsync(new GetItemRequest { TableName = tableName, Key = Item(("pk", S(loserKey))), ConsistentRead = true });
        Ensure(
            loserOrder.Item["status"].S == "RESERVED",
            $"expected loser order {Py.Repr(loserKey)} to be untouched at RESERVED, got {Py.Repr(loserOrder.Item["status"].S)}");
        var loserReservation = await ddb.GetItemAsync(new GetItemRequest { TableName = tableName, Key = Item(("pk", S(ReservationKeys[loserKey]))), ConsistentRead = true });
        Ensure(
            loserReservation.Item["status"].S == "HELD",
            $"expected loser's reservation hold to remain HELD, got {Py.Repr(loserReservation.Item["status"].S)}");
    }
}

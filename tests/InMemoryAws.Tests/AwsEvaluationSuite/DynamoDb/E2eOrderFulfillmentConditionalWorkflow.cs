// Translated from behavioral/dynamodb/check_e2e_order_fulfillment_conditional_workflow.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.e2e-order-fulfillment-conditional-workflow</summary>
[Category("AwsEvaluationSuite")]
public sealed class E2eOrderFulfillmentConditionalWorkflow
{
    /// <summary>
    /// A conditional PutItem reservation, a conditional UpdateItem status change and a TransactWriteItems settlement
    /// leave the stock reduced by exactly the reserved quantity, the order FULFILLED and the reservation deleted.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.e2e-order-fulfillment-conditional-workflow")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var tableName = FidelityContext.UniqueName("sim2real-behav-fulfill-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags =
            [
                new Tag { Key = "sim2real:managed", Value = "true" },
                new Tag { Key = "sim2real:prefix", Value = "sim2real-behav" },
            ],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(tableName));
        await ddb.WaitForTableExists(tableName, delaySeconds: 2, maxAttempts: 250);

        const string stockKey = "STOCK#sku-1";
        const string orderKey = "ORDER#1";
        const string reservationKey = "RESERVATION#1";
        const int reserveQty = 2;
        const int initialQty = 5;
        static string Str(int value) => value.ToString(CultureInfo.InvariantCulture);

        await ddb.PutItemAsync(tableName, Item(("pk", S(stockKey)), ("quantity", N(Str(initialQty)))));
        await ddb.PutItemAsync(tableName, Item(("pk", S(orderKey)), ("status", S("PENDING")), ("sku", S(stockKey))));

        // Step 1: reserve stock via a conditional PutItem -- the hold record may only be created
        // once (guards against a duplicate reservation attempt for the same order).
        await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = tableName,
            Item = Item(("pk", S(reservationKey)), ("order", S(orderKey)), ("qty", N(Str(reserveQty))), ("status", S("HELD"))),
            ConditionExpression = "attribute_not_exists(pk)",
        });

        // Step 2: advance the order status, conditioned on it still being PENDING.
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = tableName,
            Key = Item(("pk", S(orderKey))),
            UpdateExpression = "SET #s = :new_status",
            ConditionExpression = "#s = :expected_status",
            ExpressionAttributeNames = Names(("#s", "status")),
            ExpressionAttributeValues = Item((":new_status", S("RESERVED")), (":expected_status", S("PENDING"))),
        });

        // Step 3: atomic settlement -- decrement stock, flip the order to FULFILLED, and release the
        // reservation hold, all-or-nothing.
        await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
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
                        ExpressionAttributeValues = Item((":qty", N(Str(reserveQty)))),
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
        });

        // Step 4: the final state is consistent across all three items.
        var stockAfter = await ddb.GetItemAsync(new GetItemRequest { TableName = tableName, Key = Item(("pk", S(stockKey))), ConsistentRead = true });
        var expectedQty = Str(initialQty - reserveQty);
        Ensure(
            stockAfter.Item["quantity"].N == expectedQty,
            $"expected stock quantity {Py.Repr(expectedQty)} after settlement, saw {Py.Repr(stockAfter.Item)}");

        var orderAfter = await ddb.GetItemAsync(new GetItemRequest { TableName = tableName, Key = Item(("pk", S(orderKey))), ConsistentRead = true });
        Ensure(orderAfter.Item["status"].S == "FULFILLED", $"expected order status 'FULFILLED' after settlement, saw {Py.Repr(orderAfter.Item)}");

        var reservationAfter = await ddb.GetItemAsync(new GetItemRequest { TableName = tableName, Key = Item(("pk", S(reservationKey))), ConsistentRead = true });
        Ensure(reservationAfter.Item is null, $"reservation hold was not released: {Py.Repr(reservationAfter.Item)}");
    }
}

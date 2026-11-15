// Translated from behavioral/dynamodb/check_e2e_shopping_cart_workflow.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.e2e-shopping-cart-workflow</summary>
[Category("AwsEvaluationSuite")]
public sealed class E2eShoppingCartWorkflow
{
    private const string CheckId = "e2e-shopping-cart-workflow";

    /// <summary>Shopping cart: BatchWrite items, Query, UpdateItem quantity, DeleteItem, verify.</summary>
    [Test]
    [DisplayName("dynamodb.e2e-shopping-cart-workflow")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("user_id", KeyType.HASH), new KeySchemaElement("item_id", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("user_id", ScalarAttributeType.S), new AttributeDefinition("item_id", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        var items = Enumerable.Range(0, 5)
            .Select(i => Item(
                ("user_id", S("user-1")),
                ("item_id", S(string.Create(CultureInfo.InvariantCulture, $"item-{i:D3}"))),
                ("quantity", N("1")),
                ("name", S(string.Create(CultureInfo.InvariantCulture, $"Product {i}")))))
            .ToList();
        var requests = items.Select(item => new WriteRequest { PutRequest = new PutRequest { Item = item } }).ToList();
        await ddb.BatchWriteItemAsync(new BatchWriteItemRequest { RequestItems = new() { [table] = requests } });
        var resp = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "user_id = :uid",
            ExpressionAttributeValues = Item((":uid", S("user-1"))),
            ConsistentRead = true,
        });
        Ensure(resp.Count == 5, $"Expected 5 items, got {resp.Count}");
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("user_id", S("user-1")), ("item_id", S("item-002"))),
            UpdateExpression = "SET quantity = :q",
            ExpressionAttributeValues = Item((":q", N("3"))),
        });
        await ddb.DeleteItemAsync(table, Item(("user_id", S("user-1")), ("item_id", S("item-004"))));
        var resp2 = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "user_id = :uid",
            ExpressionAttributeValues = Item((":uid", S("user-1"))),
            ConsistentRead = true,
        });
        Ensure(resp2.Count == 4, $"Expected 4 items after delete, got {resp2.Count}");
        var updated = (await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = table,
            Key = Item(("user_id", S("user-1")), ("item_id", S("item-002"))),
            ConsistentRead = true,
        })).Item;
        Ensure(Py.Equal(updated["quantity"], N("3")), $"Expected quantity 3, got {Py.Repr(updated["quantity"])}");
        var deleted = (await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = table,
            Key = Item(("user_id", S("user-1")), ("item_id", S("item-004"))),
            ConsistentRead = true,
        })).Item;
        Ensure(deleted is null, "item-004 should be deleted");
        var error = await FidelityContext.CaptureError(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("user_id", S("user-1")), ("item_id", S("item-004"))),
            UpdateExpression = "SET quantity = :q",
            ConditionExpression = "attribute_exists(item_id)",
            ExpressionAttributeValues = Item((":q", N("9"))),
        }));
        if (error is null)
        {
            throw Fail("Expected ConditionalCheckFailedException updating deleted item-004, call succeeded");
        }

        var code = error.Code;
        Ensure(code == "ConditionalCheckFailedException", $"Expected ConditionalCheckFailedException updating deleted item-004, got {code}");
        var message = error.Message;
        Ensure(message == "The conditional request failed", $"Expected the conditional-check message, got {Py.Repr(message)}");
    }
}

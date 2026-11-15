// Translated from behavioral/dynamodb/check_update_item_set_list_preserves_element_order.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-set-list-preserves-element-order</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemSetListPreservesElementOrder
{
    /// <summary>
    /// A SET writing a list preserves element order on read-back, so index 0 stays index 0. An engine
    /// storing the list as an unordered collection fails.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-set-list-preserves-element-order")]
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
        await ddb.PutItemAsync(table, Item(("pk", S("lst"))));
        var res = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("lst"))),
            UpdateExpression = "SET l = :l",
            ExpressionAttributeValues = Item((":l", L(N("3"), N("1"), N("2")))),
            ReturnValues = ReturnValue.ALL_NEW,
        });
        Ensure(
            Py.Equal(res.Attributes["l"], L(N("3"), N("1"), N("2"))),
            $"the list must come back in the order written, got {Py.Repr(res.Attributes["l"])}");
        var read = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("lst"))), ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(read["l"], L(N("3"), N("1"), N("2"))),
            $"order must survive the round trip to storage, got {Py.Repr(read["l"])}");
    }
}

// Translated from behavioral/dynamodb/check_delete_item_condition_expression_invalid_attribute_name_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.delete-item-condition-expression-invalid-attribute-name-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteItemConditionExpressionInvalidAttributeNameRejected
{
    private const string Check = "delete-item-condition-expression-invalid-attribute-name-rejected";

    /// <summary>Call fn(); return the botocore (Error.Code, Error.Message). An accepted call raises.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task<object>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected a ClientError, but the call SUCCEEDED and returned {result}");
        }

        return (error.Code, error.Message);
    }

    /// <summary>
    /// DeleteItem whose ConditionExpression cites an undeclared attribute name is rejected.
    ///
    /// The item survives the ValidationException; a properly declared condition deletes it.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.delete-item-condition-expression-invalid-attribute-name-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var tableName = FidelityContext.UniqueName("sim2real-behav-diean-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(tableName));
        await ddb.WaitForTableExists(tableName, delaySeconds: 2, maxAttempts: 250);

        var key = Item(("pk", S("item-1")));
        await ddb.PutItemAsync(tableName, Item(("pk", S("item-1")), ("status", S("active"))));

        // ConditionExpression references "#missing" but ExpressionAttributeNames never defines it.
        var (code, message) = await Error(async () => await ddb.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = tableName,
            Key = key,
            ConditionExpression = "#missing = :v",
            ExpressionAttributeNames = Names(("#other", "status")),
            ExpressionAttributeValues = Item((":v", S("active"))),
        }));
        Ensure(code == "ValidationException", $"expected ValidationException, got {Py.Repr(code)}");
        const string expectedMsg =
            "1 validation error detected: Invalid ConditionExpression: An expression attribute name " +
            "used in the document path is not defined; attribute name: #missing";
        Ensure(message == expectedMsg, $"expected message {Py.Repr(expectedMsg)}, got {Py.Repr(message)}");

        // Effect absent: the item was not deleted by the rejected call.
        var item = (await ddb.GetItemAsync(new GetItemRequest { TableName = tableName, Key = key, ConsistentRead = true })).Item;
        Ensure(item["status"].S == "active", "assert item['status']['S'] == 'active'");

        // Positive control: the same delete with a properly declared attribute name succeeds.
        await ddb.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = tableName,
            Key = key,
            ConditionExpression = "#s = :v",
            ExpressionAttributeNames = Names(("#s", "status")),
            ExpressionAttributeValues = Item((":v", S("active"))),
        });
        var resp = await ddb.GetItemAsync(new GetItemRequest { TableName = tableName, Key = key, ConsistentRead = true });
        Ensure(resp.Item is null, "assert 'Item' not in resp");
    }
}

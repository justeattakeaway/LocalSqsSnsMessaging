// Translated from behavioral/dynamodb/check_update_expression_delete_from_set.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-expression-delete-from-set</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateExpressionDeleteFromSet
{
    /// <summary>UpdateExpression DELETE removes elements from a String Set.</summary>
    [Test]
    [DisplayName("dynamodb.update-expression-delete-from-set")]
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
        await ddb.PutItemAsync(table, Item(("pk", S("upd-del")), ("tags", SS("x", "y", "z"))));
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("upd-del"))),
            UpdateExpression = "DELETE tags :rm",
            ExpressionAttributeValues = Item((":rm", SS("y"))),
        });
        var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("upd-del"))), ConsistentRead = true })).Item;
        Ensure(
            got["tags"].SS.ToHashSet(StringComparer.Ordinal).SetEquals(["x", "z"]),
            $"DELETE tags :rm with :rm={{'y'}} must leave the surviving members {{'x', 'z'}}, got {Py.Repr(got["tags"].SS)}");
    }
}

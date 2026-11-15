// Translated from behavioral/dynamodb/check_update_expression_add_to_set.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-expression-add-to-set</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateExpressionAddToSet
{
    /// <summary>UpdateExpression ADD on a String Set adds elements.</summary>
    [Test]
    [DisplayName("dynamodb.update-expression-add-to-set")]
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
            Tags = [new Tag { Key = "sim2real", Value = "update-expression-add-to-set" }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        await ddb.PutItemAsync(table, Item(("pk", S("upd-ss")), ("tags", SS("a", "b"))));
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("upd-ss"))),
            UpdateExpression = "ADD tags :new_tags",
            ExpressionAttributeValues = Item((":new_tags", SS("b", "c"))),
        });
        var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("upd-ss"))), ConsistentRead = true })).Item;
        Ensure(
            got["tags"].SS.ToHashSet(StringComparer.Ordinal).SetEquals(["a", "b", "c"]),
            $"ADD on a String Set must union and dedup: expected {{'a', 'b', 'c'}}, got {Py.Repr(got["tags"].SS)}");
    }
}

// Translated from behavioral/dynamodb/check_update_expression_remove.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-expression-remove</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateExpressionRemove
{
    private const string CheckId = "update-expression-remove";

    /// <summary>UpdateExpression REMOVE deletes attributes from an item.</summary>
    [Test]
    [DisplayName("dynamodb.update-expression-remove")]
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
            Tags = [new Tag { Key = "sim2real-check", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        await ddb.PutItemAsync(table, Item(("pk", S("upd-rm")), ("keep", S("yes")), ("extra", S("no"))));
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("upd-rm"))),
            UpdateExpression = "REMOVE extra",
        });
        var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("upd-rm"))), ConsistentRead = true })).Item;
        Ensure(got is not null, "item vanished after REMOVE update");
        var names = got.Keys.Order(StringComparer.Ordinal).ToList();
        Ensure(!got.ContainsKey("extra"), $"REMOVE did not delete 'extra', got attributes: {Py.Repr(names)}");
        Ensure(Py.Equal(got["keep"], S("yes")), $"unrelated attribute 'keep' was mutated by REMOVE: {Py.Repr(got.GetValueOrDefault("keep"))}");
        Ensure(got.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(["pk", "keep"]), $"expected exactly {{pk, keep}} to remain, got: {Py.Repr(names)}");
    }
}

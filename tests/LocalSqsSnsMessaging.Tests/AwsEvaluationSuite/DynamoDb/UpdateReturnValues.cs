// Translated from behavioral/dynamodb/check_update_return_values.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-return-values</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateReturnValues
{
    /// <summary>
    /// UpdateItem with ReturnValues=ALL_NEW returns the full updated item, and the
    /// update is durably visible to an independent GetItem — the UpdateItem response
    /// alone is not proof the write landed.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-return-values")]
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
            Tags =
            [
                new Tag { Key = "sim2real:managed", Value = "true" },
                new Tag { Key = "sim2real:prefix", Value = "sim2real-behav" },
            ],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        var key = Item(("pk", S("rv-upd")));
        await ddb.PutItemAsync(table, Item(("pk", S("rv-upd")), ("x", N("1"))));
        var resp = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET x = :v",
            ExpressionAttributeValues = Item((":v", N("42"))),
            ReturnValues = ReturnValue.ALL_NEW,
        });
        var attrs = resp.Attributes;
        Ensure(Py.Equal(attrs["pk"], S("rv-upd")), $"UpdateItem ALL_NEW must echo the key, got {Py.Repr(attrs)}");
        Ensure(Py.Equal(attrs["x"], N("42")), $"UpdateItem ALL_NEW must return the new value, got {Py.Repr(attrs)}");

        var item = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(item is not null, $"expected key rv-upd to persist after the update, got {Py.Repr(item)}");
        Ensure(Py.Equal(item["x"], N("42")), $"expected the persisted value to be 42 after the update, got {Py.Repr(item)}");
    }
}

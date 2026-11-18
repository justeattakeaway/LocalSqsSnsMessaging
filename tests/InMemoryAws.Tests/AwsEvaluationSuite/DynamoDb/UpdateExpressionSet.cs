// Translated from behavioral/dynamodb/check_update_expression_set.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-expression-set</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateExpressionSet
{
    /// <summary>UpdateExpression SET adds/modifies attributes.</summary>
    [Test]
    [DisplayName("dynamodb.update-expression-set")]
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
        await ddb.PutItemAsync(table, Item(("pk", S("upd-set")), ("a", N("1"))));
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("upd-set"))),
            UpdateExpression = "SET a = :new, b = :b",
            ExpressionAttributeValues = Item((":new", N("2")), (":b", S("added"))),
        });
        var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("upd-set"))), ConsistentRead = true })).Item;
        Ensure(Py.Equal(got["a"], N("2")), $"SET on existing attribute did not take effect: expected a={{'N': '2'}}, got {Py.Repr(got.GetValueOrDefault("a"))}");
        Ensure(Py.Equal(got["b"], S("added")), $"SET did not add new attribute: expected b={{'S': 'added'}}, got {Py.Repr(got.GetValueOrDefault("b"))}");
    }
}

// Translated from behavioral/dynamodb/check_update_expression_add_number.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-expression-add-number</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateExpressionAddNumber
{
    private const string CheckId = "update-expression-add-number";

    /// <summary>UpdateExpression ADD on a Number attribute performs atomic increment.</summary>
    [Test]
    [DisplayName("dynamodb.update-expression-add-number")]
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
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        await ddb.PutItemAsync(table, Item(("pk", S("upd-add")), ("hits", N("10"))));
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("upd-add"))),
            UpdateExpression = "ADD hits :inc",
            ExpressionAttributeValues = Item((":inc", N("5"))),
        });
        var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("upd-add"))), ConsistentRead = true })).Item;
        Ensure(Py.Equal(got["hits"], N("15")), $"expected ADD hits :inc(5) on hits=10 to yield hits=15, got {Py.Repr(got)}");
    }
}

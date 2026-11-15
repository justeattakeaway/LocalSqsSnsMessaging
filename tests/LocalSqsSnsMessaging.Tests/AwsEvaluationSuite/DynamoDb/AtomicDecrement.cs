// Translated from behavioral/dynamodb/check_atomic_decrement.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.atomic-decrement</summary>
[Category("AwsEvaluationSuite")]
public sealed class AtomicDecrement
{
    private const string Check = "atomic-decrement";

    /// <summary>ADD with negative Number performs atomic decrement.</summary>
    [Test]
    [DisplayName("dynamodb.atomic-decrement")]
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
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        await ddb.PutItemAsync(table, Item(("pk", S("dec")), ("balance", N("100"))));
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("dec"))),
            UpdateExpression = "ADD balance :dec",
            ExpressionAttributeValues = Item((":dec", N("-30"))),
        });
        var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("dec"))), ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(got["balance"], N("70")),
            $"expected balance {{'N': '70'}} after ADD -30, got {Py.Repr(got.GetValueOrDefault("balance"))}");
    }
}

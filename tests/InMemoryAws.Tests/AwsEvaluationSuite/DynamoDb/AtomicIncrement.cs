// Translated from behavioral/dynamodb/check_atomic_increment.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.atomic-increment</summary>
[Category("AwsEvaluationSuite")]
public sealed class AtomicIncrement
{
    private const string Check = "atomic-increment";

    /// <summary>ADD on Number attribute performs atomic increment correctly.</summary>
    [Test]
    [DisplayName("dynamodb.atomic-increment")]
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
            Tags = [new Tag { Key = "sim2real-check", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        await ddb.PutItemAsync(table, Item(("pk", S("counter")), ("hits", N("0"))));
        for (var i = 0; i < 5; i++)
        {
            await ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = Item(("pk", S("counter"))),
                UpdateExpression = "ADD hits :inc",
                ExpressionAttributeValues = Item((":inc", N("1"))),
            });
        }

        var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("counter"))), ConsistentRead = true })).Item;
        Ensure(got is not null, "counter item vanished after 5 ADD increments");
        Ensure(
            Py.Equal(got["hits"], N("5")),
            $"expected hits=5 after five +1 ADD updates, got {Py.Repr(got.GetValueOrDefault("hits"))}");
    }
}

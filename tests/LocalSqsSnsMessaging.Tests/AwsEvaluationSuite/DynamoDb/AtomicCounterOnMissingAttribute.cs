// Translated from behavioral/dynamodb/check_atomic_counter_on_missing_attribute.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.atomic-counter-on-missing-attribute</summary>
[Category("AwsEvaluationSuite")]
public sealed class AtomicCounterOnMissingAttribute
{
    private const string Check = "atomic-counter-on-missing-attribute";

    /// <summary>ADD on a non-existent Number attribute initializes it to the increment value.</summary>
    [Test]
    [DisplayName("dynamodb.atomic-counter-on-missing-attribute")]
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
        await ddb.PutItemAsync(table, Item(("pk", S("new-counter"))));
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("new-counter"))),
            UpdateExpression = "ADD visits :inc",
            ExpressionAttributeValues = Item((":inc", N("1"))),
        });
        var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("new-counter"))), ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(got["visits"], N("1")),
            $"expected visits={{'N': '1'}} after ADD on missing attribute, got {Py.Repr(got.GetValueOrDefault("visits"))}");
    }
}

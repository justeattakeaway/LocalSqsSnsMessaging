// Translated from behavioral/dynamodb/check_condition_on_missing_item.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.condition-on-missing-item</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConditionOnMissingItem
{
    private const string ErrConditionalCheckFailed = "ConditionalCheckFailedException";

    /// <summary>ConditionExpression on a non-existent item raises ConditionalCheckFailedException; the same expression succeeds once the item exists.</summary>
    [Test]
    [DisplayName("dynamodb.condition-on-missing-item")]
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

        async Task<Dictionary<string, AttributeValue>?> Get(string pk) =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;

        // Positive control: attribute_exists(pk) succeeds against an item that is already there.
        const string controlKey = "has-item";
        await ddb.PutItemAsync(table, Item(("pk", S(controlKey)), ("v", S("seed"))));
        await ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = Item(("pk", S(controlKey)), ("v", S("seed2"))), ConditionExpression = "attribute_exists(pk)" });
        var controlGot = await Get(controlKey);
        Ensure(
            Py.Equal(controlGot, Item(("pk", S(controlKey)), ("v", S("seed2")))),
            $"positive control: expected the conditional put to land, got {Py.Repr(controlGot)}");

        // Break exactly one thing: the same condition against a pk that was never created.
        const string targetKey = "no-such-item";
        var error = await FidelityContext.CaptureError(() => ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = Item(("pk", S(targetKey)), ("v", S("x"))),
            ConditionExpression = "attribute_exists(pk)",
        }));
        var (code, message) = (error?.Code, error?.Message);
        Ensure(code == ErrConditionalCheckFailed, $"expected {ErrConditionalCheckFailed}, got {Py.Repr(code)}");
        Ensure(message == "The conditional request failed", $"expected AWS's exact ConditionalCheckFailedException message, got {Py.Repr(message)}");

        // Effect absent: the rejected put created nothing.
        var absent = await Get(targetKey);
        Ensure(absent is null, $"expected no item at {Py.Repr(targetKey)} after the rejected put, got {Py.Repr(absent)}");

        // Restore: create the item, then the identical call now succeeds.
        await ddb.PutItemAsync(table, Item(("pk", S(targetKey)), ("v", S("seed"))));
        await ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = Item(("pk", S(targetKey)), ("v", S("x"))), ConditionExpression = "attribute_exists(pk)" });
        var restored = await Get(targetKey);
        Ensure(
            Py.Equal(restored, Item(("pk", S(targetKey)), ("v", S("x")))),
            $"expected the restored conditional put to land, got {Py.Repr(restored)}");
    }
}

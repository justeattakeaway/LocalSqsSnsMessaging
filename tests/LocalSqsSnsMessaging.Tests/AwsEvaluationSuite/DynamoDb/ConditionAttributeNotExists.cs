// Translated from behavioral/dynamodb/check_condition_attribute_not_exists.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.condition-attribute-not-exists</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConditionAttributeNotExists
{
    private const string ErrConditionalCheckFailed = "ConditionalCheckFailedException";

    /// <summary>
    /// ConditionExpression attribute_not_exists succeeds while the attribute is absent,
    /// fails once it is present, and succeeds again once it is removed — proving the
    /// condition tracks live state rather than always accepting or always rejecting.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.condition-attribute-not-exists")]
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

        Dictionary<string, AttributeValue> Key() => Item(("pk", S("cond-2")));
        async Task<Dictionary<string, AttributeValue>?> Get() =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;

        // Positive control: item exists but has no 'flag' attribute yet, so the
        // condition must be satisfied and the put must succeed.
        await ddb.PutItemAsync(table, Item(("pk", S("cond-2"))));
        await ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = Item(("pk", S("cond-2")), ("flag", S("active"))), ConditionExpression = "attribute_not_exists(flag)" });
        var got = await Get();
        Ensure(
            got is not null && Py.Equal(got.GetValueOrDefault("flag"), S("active")),
            $"positive control: expected flag=active after unconditional-attribute-absent put, got {Py.Repr(got)}");

        // Break: 'flag' is now present, so the identical condition must reject the next write.
        var error = await FidelityContext.CaptureError(() => ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = Item(("pk", S("cond-2")), ("flag", S("changed"))),
            ConditionExpression = "attribute_not_exists(flag)",
        }));
        var (code, message) = (error?.Code, error?.Message);
        Ensure(code == ErrConditionalCheckFailed, $"expected {ErrConditionalCheckFailed}, got {code ?? "None"}");
        Ensure(message == "The conditional request failed", $"expected AWS's exact ConditionalCheckFailedException message, got {Py.Repr(message)}");

        // Effect absent: an independent read shows the rejected write never landed.
        got = await Get();
        Ensure(got is not null && Py.Equal(got.GetValueOrDefault("flag"), S("active")), $"rejected put must not change state, got {Py.Repr(got)}");

        // Restore: remove 'flag' so the condition is satisfiable again.
        await ddb.UpdateItemAsync(new UpdateItemRequest { TableName = table, Key = Key(), UpdateExpression = "REMOVE flag" });

        // The identical call now succeeds against the restored state.
        await ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = Item(("pk", S("cond-2")), ("flag", S("changed"))), ConditionExpression = "attribute_not_exists(flag)" });
        got = await Get();
        Ensure(
            got is not null && Py.Equal(got.GetValueOrDefault("flag"), S("changed")),
            $"expected flag=changed after restored attribute-absent put, got {Py.Repr(got)}");
    }
}

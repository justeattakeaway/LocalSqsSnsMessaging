// Translated from behavioral/dynamodb/check_condition_attribute_exists.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.condition-attribute-exists</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConditionAttributeExists
{
    private const string ErrConditionalCheckFailed = "ConditionalCheckFailedException";

    /// <summary>
    /// ConditionExpression attribute_exists succeeds when the attribute is present, and
    /// is actually enforced (not a no-op) once that attribute is removed.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.condition-attribute-exists")]
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

        async Task<Dictionary<string, AttributeValue>?> Get() =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("cond-1"))), ConsistentRead = true })).Item;

        // --- Positive control: flag is present, so attribute_exists(flag) must let the write through.
        await ddb.PutItemAsync(table, Item(("pk", S("cond-1")), ("flag", S("active"))));
        await ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = Item(("pk", S("cond-1")), ("flag", S("updated"))), ConditionExpression = "attribute_exists(flag)" });
        var got = await Get();
        Ensure(
            got is not null && Py.Equal(got.GetValueOrDefault("flag"), S("updated")),
            $"positive control: expected flag=updated after a satisfied attribute_exists condition, got {Py.Repr(got)}");

        // --- Break exactly one thing: overwrite the item so it no longer carries `flag`.
        await ddb.PutItemAsync(table, Item(("pk", S("cond-1"))));

        // --- The identical condition must now be refused with the specific error.
        var error = await FidelityContext.CaptureError(() => ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = Item(("pk", S("cond-1")), ("flag", S("should-not-apply"))),
            ConditionExpression = "attribute_exists(flag)",
        }));
        var (code, message) = (error?.Code, error?.Message);
        Ensure(code == ErrConditionalCheckFailed, $"expected {ErrConditionalCheckFailed} once flag is absent, got {Py.Repr(code)}");
        Ensure(message == "The conditional request failed", $"expected AWS's exact ConditionalCheckFailedException message, got {Py.Repr(message)}");

        // --- Effect absent: the rejected write must not have applied.
        got = await Get();
        Ensure(got is not null && !got.ContainsKey("flag"), $"rejected conditional put must not apply; got item {Py.Repr(got)}");

        // --- Restore, then the identical call must succeed again.
        await ddb.PutItemAsync(table, Item(("pk", S("cond-1")), ("flag", S("active"))));
        await ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = Item(("pk", S("cond-1")), ("flag", S("updated"))), ConditionExpression = "attribute_exists(flag)" });
        got = await Get();
        Ensure(
            got is not null && Py.Equal(got.GetValueOrDefault("flag"), S("updated")),
            $"expected flag=updated after restoring flag and re-applying the same condition, got {Py.Repr(got)}");
    }
}

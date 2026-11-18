// Translated from behavioral/dynamodb/check_update_item_if_not_exists_size_checked_before_condition.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-if-not-exists-size-checked-before-condition</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemIfNotExistsSizeCheckedBeforeCondition
{
    private const string CheckId = "update-item-if-not-exists-size-checked-before-condition";
    private const int OversizedBytes = 409700;
    private static readonly (string Code, string Message) ErrValidation = ("ValidationException", "Item size to update has exceeded the maximum allowed size");
    private static readonly (string Code, string Message) ErrConditional = ("ConditionalCheckFailedException", "The conditional request failed");

    /// <summary>
    /// UpdateItem refuses an oversized if_not_exists operand for size before it evaluates
    /// ConditionExpression, in two Regions alike: never ConditionalCheckFailedException, whether
    /// the condition would pass, fail or is absent, and the stored item is left unchanged.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-if-not-exists-size-checked-before-condition")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbsize-");
        await CreateTable(ctx, ddb, table);
        await RunScenarios(ddb, table);

        // region-pinned: the same size-before-condition order is asserted in us-east-1.
        var ddb2 = ctx.DynamoClient(region: "us-east-1");
        var table2 = FidelityContext.UniqueName("sim2real-behav-ddbsize-east-");
        await CreateTable(ctx, ddb2, table2);
        await RunScenarios(ddb2, table2);
    }

    private static async Task ExpectError<T>(Func<Task<T>> fn, (string Code, string Message) expected)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
        }

        var got = (error.Code, error.Message);
        Ensure(got == expected, $"expected {Py.Repr(expected)}, got {Py.Repr(got)}");
    }

    private static async Task<Dictionary<string, AttributeValue>> Get(IAmazonDynamoDB ddb, string table, string pk) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item ?? [];

    /// <summary>Run the if_not_exists-size-vs-condition scenarios against one empty table.</summary>
    private static async Task RunScenarios(IAmazonDynamoDB ddb, string table)
    {
        // Positive control: a small if_not_exists update succeeds when its condition passes.
        await ddb.PutItemAsync(table, Item(("pk", S("ctrl-pass")), ("flag", Bool(true))));
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("ctrl-pass"))),
            UpdateExpression = "SET note = if_not_exists(note, :v)",
            ConditionExpression = "flag = :true",
            ExpressionAttributeValues = Item((":v", S("ok")), (":true", Bool(true))),
        });
        var passed = await Get(ddb, table, "ctrl-pass");
        Ensure(passed.GetValueOrDefault("note")?.S == "ok", $"positive control note must round-trip, got {Py.Repr(passed)}");

        // Baseline: the same small update with a condition that genuinely fails raises
        // ConditionalCheckFailedException, proving the condition itself would fire here.
        await ddb.PutItemAsync(table, Item(("pk", S("ctrl-fail")), ("flag", Bool(false))));
        await ExpectError(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("ctrl-fail"))),
            UpdateExpression = "SET note = if_not_exists(note, :v)",
            ConditionExpression = "flag = :true",
            ExpressionAttributeValues = Item((":v", S("ok")), (":true", Bool(true))),
        }), ErrConditional);
        var baseline = await Get(ddb, table, "ctrl-fail");
        Ensure(!baseline.ContainsKey("note"), $"a genuinely failed condition must leave no trace, got {Py.Repr(baseline)}");

        var oversized = new string('x', OversizedBytes);

        // An oversized if_not_exists operand is refused for size whether its ConditionExpression
        // would pass...
        await ddb.PutItemAsync(table, Item(("pk", S("cond-pass")), ("flag", Bool(true))));
        await ExpectError(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("cond-pass"))),
            UpdateExpression = "SET bigattr = if_not_exists(bigattr, :v)",
            ConditionExpression = "flag = :true",
            ExpressionAttributeValues = Item((":v", S(oversized)), (":true", Bool(true))),
        }), ErrValidation);
        var afterPass = await Get(ddb, table, "cond-pass");
        Ensure(!afterPass.ContainsKey("bigattr"), $"a size refusal must leave no trace, got {Py.Repr(afterPass)}");

        // ...or would fail...
        await ddb.PutItemAsync(table, Item(("pk", S("cond-fail")), ("flag", Bool(false))));
        await ExpectError(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("cond-fail"))),
            UpdateExpression = "SET bigattr = if_not_exists(bigattr, :v)",
            ConditionExpression = "flag = :true",
            ExpressionAttributeValues = Item((":v", S(oversized)), (":true", Bool(true))),
        }), ErrValidation);
        var afterFail = await Get(ddb, table, "cond-fail");
        Ensure(!afterFail.ContainsKey("bigattr"), $"a size refusal must leave no trace, got {Py.Repr(afterFail)}");

        // ...or is absent entirely.
        await ddb.PutItemAsync(table, Item(("pk", S("cond-absent"))));
        await ExpectError(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("cond-absent"))),
            UpdateExpression = "SET bigattr = if_not_exists(bigattr, :v)",
            ExpressionAttributeValues = Item((":v", S(oversized))),
        }), ErrValidation);
        var afterAbsent = await Get(ddb, table, "cond-absent");
        Ensure(!afterAbsent.ContainsKey("bigattr"), $"a size refusal must leave no trace, got {Py.Repr(afterAbsent)}");
    }

    private static async Task CreateTable(FidelityContext ctx, IAmazonDynamoDB ddb, string table)
    {
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 30);
    }
}

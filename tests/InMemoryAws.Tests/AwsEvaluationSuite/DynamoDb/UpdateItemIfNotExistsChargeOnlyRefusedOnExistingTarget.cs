// Translated from behavioral/dynamodb/check_update_item_if_not_exists_charge_only_refused_on_existing_target.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-if-not-exists-charge-only-refused-on-existing-target</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemIfNotExistsChargeOnlyRefusedOnExistingTarget
{
    private const string Check = "update-item-if-not-exists-charge-only-refused-on-existing-target";
    private static readonly (string Code, string Message) ErrValidation = ("ValidationException", "Item size to update has exceeded the maximum allowed size");
    private const int OversizedBytes = 409700;

    /// <summary>The (code, message) <paramref name="fn"/> raises; an unexpected success raises instead.</summary>
    private static async Task<(string? Code, string? Message)> CodeAndMessage(Func<Task<object>> fn)
    {
        var (result, err) = await FidelityContext.Capture(fn);
        return err is null ? throw Fail($"expected the call to be refused, but it succeeded: {result}") : (err.Code, err.Message);
    }

    private static async Task ExpectError(Func<Task<object>> fn, (string Code, string Message) expected)
    {
        var got = await CodeAndMessage(fn);
        Ensure(got == expected, $"expected {Py.Repr(expected)}, got {Py.Repr(got)}");
    }

    /// <summary>
    /// UpdateItem refuses an oversized if_not_exists operand for size even when the target
    /// attribute already exists and the not-exists branch never fires, with a passing or
    /// absent ConditionExpression alike.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ServiceQuotas.html#limits-items
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-if-not-exists-charge-only-refused-on-existing-target")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbinecharge-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 30);
        AttributeValue Oversized() => S(new string('x', OversizedBytes));

        async Task<Dictionary<string, AttributeValue>> Read(string pk) =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;

        // Positive control: with a small operand, if_not_exists on an existing attribute is a
        // no-op -- the existing value survives untouched, proving the not-exists branch is not
        // taken when the target already exists.
        await ddb.PutItemAsync(table, Item(("pk", S("ctrl-noop")), ("bigattr", S("orig")), ("flag", Bool(true))));
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("ctrl-noop"))),
            UpdateExpression = "SET bigattr = if_not_exists(bigattr, :v)",
            ConditionExpression = "flag = :true",
            ExpressionAttributeValues = Item((":v", S("small")), (":true", Bool(true))),
        });
        var noop = await Read("ctrl-noop");
        Ensure(noop["bigattr"].S == "orig", $"if_not_exists on an existing attribute must keep the existing value, got {Py.Repr(noop)}");

        // The target already exists with a small value, so an accepted update would leave the
        // item unchanged -- yet the oversized operand is still refused for size, with a passing
        // ConditionExpression.
        await ddb.PutItemAsync(table, Item(("pk", S("cond-pass")), ("bigattr", S("orig")), ("flag", Bool(true))));
        await ExpectError(
            async () => await ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = Item(("pk", S("cond-pass"))),
                UpdateExpression = "SET bigattr = if_not_exists(bigattr, :v)",
                ConditionExpression = "flag = :true",
                ExpressionAttributeValues = Item((":v", Oversized()), (":true", Bool(true))),
            }),
            ErrValidation);
        var afterPass = await Read("cond-pass");
        Ensure(afterPass["bigattr"].S == "orig", $"a size refusal must leave the existing value untouched, got {Py.Repr(afterPass)}");

        // ...and with no ConditionExpression at all.
        await ddb.PutItemAsync(table, Item(("pk", S("cond-absent")), ("bigattr", S("orig"))));
        await ExpectError(
            async () => await ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = Item(("pk", S("cond-absent"))),
                UpdateExpression = "SET bigattr = if_not_exists(bigattr, :v)",
                ExpressionAttributeValues = Item((":v", Oversized())),
            }),
            ErrValidation);
        var afterAbsent = await Read("cond-absent");
        Ensure(afterAbsent["bigattr"].S == "orig", $"a size refusal must leave the existing value untouched, got {Py.Repr(afterAbsent)}");
    }
}

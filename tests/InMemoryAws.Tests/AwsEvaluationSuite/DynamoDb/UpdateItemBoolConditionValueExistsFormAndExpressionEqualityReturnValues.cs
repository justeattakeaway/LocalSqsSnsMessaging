// Translated from behavioral/dynamodb/check_update_item_bool_condition_value_exists_form_and_expression_equality_return_values.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-bool-condition-value-exists-form-and-expression-equality-return-values</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemBoolConditionValueExistsFormAndExpressionEqualityReturnValues
{
    private const string Check = "update-item-bool-condition-value-exists-form-and-expression-equality-return-values";
    private static readonly (string Code, string Message) ErrCcfe = ("ConditionalCheckFailedException", "The conditional request failed");

    /// <summary>The error code and message <paramref name="fn"/> raises; an unexpected success raises.</summary>
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

    private static Dictionary<string, AttributeValueUpdate> MarkerApplied() =>
        new(StringComparer.Ordinal) { ["marker"] = new AttributeValueUpdate { Value = S("applied"), Action = AttributeAction.PUT } };

    /// <summary>
    /// UpdateItem's legacy Expected implicit Value form, its explicit Exists+Value form, and a
    /// ConditionExpression equality all gate on a BOOL attribute the same way; a passing
    /// ConditionExpression equality returns UPDATED_NEW's post-update BOOL, not the pre-update one.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/LegacyConditionalParameters.Expected.html
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Expressions.ConditionExpressions.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-bool-condition-value-exists-form-and-expression-equality-return-values")]
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
        await ddb.WaitForTableExists(table, delaySeconds: 1, maxAttempts: 60);

        async Task<Dictionary<string, AttributeValue>?> Read(string pk) =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;

        // --- legacy Expected, implicit Value form: no ComparisonOperator, no Exists, so AWS treats
        // the attribute as required to exist and equal Value. ---
        await ddb.PutItemAsync(table, Item(("pk", S("implicit-match")), ("flag", Bool(true))));
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("implicit-match"))),
            AttributeUpdates = MarkerApplied(),
            Expected = new() { ["flag"] = new ExpectedAttributeValue { Value = Bool(true) } },
        });
        var got = await Read("implicit-match");
        Ensure(
            Py.Equal(got, Item(("pk", S("implicit-match")), ("flag", Bool(true)), ("marker", S("applied")))),
            $"implicit Value-form match must let the write land, got {Py.Repr(got)}");

        await ddb.PutItemAsync(table, Item(("pk", S("implicit-mismatch")), ("flag", Bool(false))));
        await ExpectError(
            async () => await ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = Item(("pk", S("implicit-mismatch"))),
                AttributeUpdates = MarkerApplied(),
                Expected = new() { ["flag"] = new ExpectedAttributeValue { Value = Bool(true) } },
            }),
            ErrCcfe);
        got = await Read("implicit-mismatch");
        Ensure(
            Py.Equal(got, Item(("pk", S("implicit-mismatch")), ("flag", Bool(false)))),
            $"a rejected implicit Value-form UpdateItem must leave the item unchanged, got {Py.Repr(got)}");

        // --- legacy Expected, explicit Exists+Value form on BOOL. ---
        await ddb.PutItemAsync(table, Item(("pk", S("explicit-match")), ("flag", Bool(false))));
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("explicit-match"))),
            AttributeUpdates = MarkerApplied(),
            Expected = new() { ["flag"] = new ExpectedAttributeValue { Exists = true, Value = Bool(false) } },
        });
        got = await Read("explicit-match");
        Ensure(
            Py.Equal(got, Item(("pk", S("explicit-match")), ("flag", Bool(false)), ("marker", S("applied")))),
            $"explicit Exists+Value match must let the write land, got {Py.Repr(got)}");

        await ddb.PutItemAsync(table, Item(("pk", S("explicit-mismatch")), ("flag", Bool(true))));
        await ExpectError(
            async () => await ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = Item(("pk", S("explicit-mismatch"))),
                AttributeUpdates = MarkerApplied(),
                Expected = new() { ["flag"] = new ExpectedAttributeValue { Exists = true, Value = Bool(false) } },
            }),
            ErrCcfe);
        got = await Read("explicit-mismatch");
        Ensure(
            Py.Equal(got, Item(("pk", S("explicit-mismatch")), ("flag", Bool(true)))),
            $"a rejected explicit Exists+Value UpdateItem must leave the item unchanged, got {Py.Repr(got)}");

        // --- ConditionExpression equality on BOOL, gating a SET that flips the same attribute:
        // UPDATED_NEW must carry the post-update BOOL, not the precondition's BOOL. ---
        await ddb.PutItemAsync(table, Item(("pk", S("expr-match")), ("flag", Bool(true))));
        var resp = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("expr-match"))),
            UpdateExpression = "SET flag = :new",
            ConditionExpression = "flag = :cond",
            ExpressionAttributeValues = Item((":new", Bool(false)), (":cond", Bool(true))),
            ReturnValues = ReturnValue.UPDATED_NEW,
        });
        Ensure(
            Py.Equal(resp.Attributes, Item(("flag", Bool(false)))),
            $"UPDATED_NEW after a passing BOOL equality condition must carry the new BOOL, got {Py.Repr(resp.Attributes)}");
        got = await Read("expr-match");
        Ensure(
            Py.Equal(got, Item(("pk", S("expr-match")), ("flag", Bool(false)))),
            $"a passing ConditionExpression equality on BOOL did not land, got {Py.Repr(got)}");

        await ddb.PutItemAsync(table, Item(("pk", S("expr-mismatch")), ("flag", Bool(false))));
        await ExpectError(
            async () => await ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = Item(("pk", S("expr-mismatch"))),
                UpdateExpression = "SET flag = :new",
                ConditionExpression = "flag = :cond",
                ExpressionAttributeValues = Item((":new", Bool(true)), (":cond", Bool(true))),
                ReturnValues = ReturnValue.UPDATED_NEW,
            }),
            ErrCcfe);
        got = await Read("expr-mismatch");
        Ensure(
            Py.Equal(got, Item(("pk", S("expr-mismatch")), ("flag", Bool(false)))),
            $"a rejected ConditionExpression equality on BOOL must leave the item unchanged, got {Py.Repr(got)}");
    }
}

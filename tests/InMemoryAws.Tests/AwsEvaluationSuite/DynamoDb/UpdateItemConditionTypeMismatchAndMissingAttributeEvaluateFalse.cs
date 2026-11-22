// Translated from behavioral/dynamodb/check_update_item_condition_type_mismatch_and_missing_attribute_evaluate_false.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-condition-type-mismatch-and-missing-attribute-evaluate-false</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemConditionTypeMismatchAndMissingAttributeEvaluateFalse
{
    private const string Check = "update-item-condition-type-mismatch-and-missing-attribute-evaluate-false";
    private static readonly (string Code, string Message) Ccf = ("ConditionalCheckFailedException", "The conditional request failed");

    /// <summary>(Error.Code, Error.Message) of the error fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task<object>> fn)
    {
        var (result, err) = await FidelityContext.Capture(fn);
        return err is null ? throw Fail($"expected the call to be refused, but it succeeded: {result}") : (err.Code, err.Message);
    }

    /// <summary>Assert fn() raises exactly (code, message).</summary>
    private static async Task ExpectError(Func<Task<object>> fn, (string Code, string Message) expected)
    {
        var got = await Error(fn);
        Ensure(got == expected, $"expected {Py.Repr(expected)}, got {Py.Repr(got)}");
    }

    /// <summary>
    /// An UpdateItem ConditionExpression evaluates false, never a type error, for begins_with
    /// against a stored N, for a cross-type N/S equality, and for a comparator on a missing
    /// attribute; each refuses with ConditionalCheckFailedException and leaves the item unchanged.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Expressions.OperatorsAndFunctions.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-condition-type-mismatch-and-missing-attribute-evaluate-false")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-condmismatch-");
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

        var key = Item(("pk", S("fixed-item")));
        var original = Item(("pk", S("fixed-item")), ("num", N("42")), ("txt", S("foobar")));
        await ddb.PutItemAsync(table, original);

        async Task<Dictionary<string, AttributeValue>> Read() =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;

        UpdateItemRequest Update(string expression, string condition, Dictionary<string, AttributeValue> values) => new()
        {
            TableName = table,
            Key = key,
            UpdateExpression = expression,
            ConditionExpression = condition,
            ExpressionAttributeValues = values,
        };

        Dictionary<string, AttributeValue> With(Dictionary<string, AttributeValue> item, string name, AttributeValue value) =>
            new(item, StringComparer.Ordinal) { [name] = value };

        // --- begins_with against a stored N attribute, with a correctly-typed S operand ---
        await ExpectError(
            async () => await ddb.UpdateItemAsync(Update("SET begins_ok = :u", "begins_with(num, :p)", Item((":u", Bool(true)), (":p", S("4"))))),
            Ccf);
        var after = await Read();
        Ensure(
            Py.Equal(after, original),
            $"begins_with(num, :p) with num stored as N must leave the item unchanged, got {Py.Repr(after)}");

        // Positive control, same function: begins_with against a correctly-typed S attribute holds.
        await ddb.UpdateItemAsync(Update("SET begins_ok = :u", "begins_with(txt, :p)", Item((":u", Bool(true)), (":p", S("foo")))));
        var afterControl1 = await Read();
        var expected1 = With(original, "begins_ok", Bool(true));
        Ensure(
            Py.Equal(afterControl1, expected1),
            "begins_with(txt, :p) over a matching S attribute should have applied the update, " +
            $"got {Py.Repr(afterControl1)}, expected {Py.Repr(expected1)}");

        // --- cross-type equality: num stored as N, operand the same digits as S ---
        await ExpectError(
            async () => await ddb.UpdateItemAsync(Update("SET eq_ok = :u", "num = :s", Item((":u", Bool(true)), (":s", S("42"))))),
            Ccf);
        after = await Read();
        Ensure(
            Py.Equal(after, expected1),
            "num = :s with num (N) and :s (S) holding the same digits must leave the item " +
            $"unchanged, got {Py.Repr(after)}");

        // Positive control, same comparator: num = :n with a correctly-typed N operand holds.
        await ddb.UpdateItemAsync(Update("SET eq_ok = :u", "num = :n", Item((":u", Bool(true)), (":n", N("42")))));
        var afterControl2 = await Read();
        var expected2 = With(expected1, "eq_ok", Bool(true));
        Ensure(
            Py.Equal(afterControl2, expected2),
            "num = :n with a correctly-typed N operand should have applied the update, " +
            $"got {Py.Repr(afterControl2)}, expected {Py.Repr(expected2)}");

        // --- comparator on a missing attribute: '>' treats it as absent and is false ---
        await ExpectError(
            async () => await ddb.UpdateItemAsync(Update("SET gt_ok = :u", "score > :v", Item((":u", Bool(true)), (":v", N("10"))))),
            Ccf);
        after = await Read();
        Ensure(
            Py.Equal(after, expected2),
            $"'score > :v' against a never-set attribute must leave the item unchanged, got {Py.Repr(after)}");

        // Positive control, same missing attribute: '<>' treats it as not-equal and the write applies.
        await ddb.UpdateItemAsync(Update("SET gt_ok = :u", "score <> :v", Item((":u", Bool(true)), (":v", N("10")))));
        var afterControl3 = await Read();
        var expected3 = With(expected2, "gt_ok", Bool(true));
        Ensure(
            Py.Equal(afterControl3, expected3),
            "'score <> :v' against a missing attribute should have applied the update, " +
            $"got {Py.Repr(afterControl3)}, expected {Py.Repr(expected3)}");
    }
}

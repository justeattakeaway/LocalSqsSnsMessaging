// Translated from behavioral/dynamodb/check_number_precision_limited_to_38_digits.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.number-precision-limited-to-38-digits</summary>
[Category("AwsEvaluationSuite")]
public sealed class NumberPrecisionLimitedTo38Digits
{
    private const string CheckId = "number-precision-limited-to-38-digits";
    private const string ErrValidation = "ValidationException";
    private const string PrecisionMessage = "Attempting to store more than 38 significant digits in a Number";

    /// <summary>The botocore error code and message <paramref name="fn"/> raises; an unexpected success raises.</summary>
    private static async Task<(string? Code, string? Message)> Code<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"real AWS rejects this call, but it SUCCEEDED and returned {Py.Repr(result)}");
    }

    /// <summary>
    /// A 38-significant-digit N round-trips exactly; 39 digits (scalar or NS member) is rejected
    /// with ValidationException and not stored. N is an exact decimal, so a double-based store fails.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.number-precision-limited-to-38-digits")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        async Task<GetItemResponse> Get(string table, Dictionary<string, AttributeValue> key) =>
            await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true });

        // Compared as a string: comparing as a float would itself lose the precision under test.
        var atLimit = new string('9', 38);
        await ddb.PutItemAsync(t, Item(("pk", S("at-limit")), ("n", N(atLimit))));
        var got = (await Get(t, Item(("pk", S("at-limit"))))).Item ?? [];
        Ensure(Py.Equal(got.GetValueOrDefault("n"), N(atLimit)), $"a 38-digit N must round-trip digit-for-digit, got {Py.Repr(got.GetValueOrDefault("n"))}");

        var over = new string('9', 39);
        var (code, message) = await Code(() => ddb.PutItemAsync(t, Item(("pk", S("over")), ("n", N(over)))));
        Ensure(code == ErrValidation, $"a 39-digit N exceeds supported precision and must be {ErrValidation}, got {Py.Repr(code)}");
        Ensure(message == "1 validation error detected: " + PrecisionMessage, $"expected AWS's exact ValidationException message, got {Py.Repr(message)}");
        Ensure((await Get(t, Item(("pk", S("over"))))).Item is null, "a number rejected for precision must not be stored");

        // Significant digits, not character count: a 38-significant-digit mantissa is
        // legal even though the decimal point makes the literal 39 characters long.
        var fractional = "1." + new string('7', 37);
        await ddb.PutItemAsync(t, Item(("pk", S("fractional")), ("n", N(fractional))));
        got = (await Get(t, Item(("pk", S("fractional"))))).Item ?? [];
        Ensure(Py.Equal(got.GetValueOrDefault("n"), N(fractional)), $"a 38-significant-digit fractional N must round-trip exactly, got {Py.Repr(got.GetValueOrDefault("n"))}");

        // A precision overflow inside a set takes the same path as a scalar.
        (code, message) = await Code(() => ddb.PutItemAsync(t, Item(("pk", S("in-set")), ("ns", NS("1", over)))));
        Ensure(code == ErrValidation, $"a 39-digit member of an NS must be {ErrValidation}, got {Py.Repr(code)}");
        Ensure(message == "1 validation error detected: " + PrecisionMessage, $"expected AWS's exact ValidationException message, got {Py.Repr(message)}");

        // A precision overflow inside a ConditionExpression value is rejected the same way as a
        // stored attribute, not as a failed condition.
        await ddb.PutItemAsync(t, Item(("pk", S("cond-base")), ("n", N("1"))));
        (code, message) = await Code(() => ddb.PutItemAsync(new PutItemRequest
        {
            TableName = t,
            Item = Item(("pk", S("cond-base")), ("n", N("2"))),
            ConditionExpression = "n <> :v",
            ExpressionAttributeValues = Item((":v", N(over))),
        }));
        Ensure(
            code == ErrValidation,
            $"a 39-digit N in a ConditionExpression value exceeds supported precision and must be {ErrValidation} (not ConditionalCheckFailedException), got {Py.Repr(code)}");
        Ensure(message == "1 validation error detected: " + PrecisionMessage, $"expected AWS's exact ValidationException message, got {Py.Repr(message)}");
        var unchanged = (await Get(t, Item(("pk", S("cond-base"))))).Item;
        Ensure(
            Py.Equal(unchanged["n"], N("1")),
            $"a write rejected for precision in its ConditionExpression value must leave the item unmodified, got {Py.Repr(unchanged["n"])}");

        // A precision overflow in a KEY attribute's value is validated the same way as an ordinary
        // attribute -- before key-schema matching, not a schema mismatch.
        var keyT = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = keyT,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.N)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(keyT));
        await ddb.WaitForTableExists(keyT, delaySeconds: 2, maxAttempts: 30);

        (code, message) = await Code(() => ddb.PutItemAsync(keyT, Item(("pk", S("k")), ("sk", N(over)))));
        Ensure(code == ErrValidation, $"a 39-digit N in a key attribute exceeds supported precision and must be {ErrValidation}, got {Py.Repr(code)}");
        Ensure(message == "1 validation error detected: " + PrecisionMessage, $"expected AWS's exact ValidationException message, got {Py.Repr(message)}");
        (code, message) = await Code(() => ddb.GetItemAsync(keyT, Item(("pk", S("k")), ("sk", N(over)))));
        Ensure(code == ErrValidation, $"GetItem on a 39-digit N key value must also be {ErrValidation}, got {Py.Repr(code)}");
        Ensure(message == PrecisionMessage, $"expected AWS's exact ValidationException message, got {Py.Repr(message)}");

        // Control: a 38-digit N key value still round-trips in the same table.
        var atLimitKey = new string('9', 38);
        await ddb.PutItemAsync(keyT, Item(("pk", S("k")), ("sk", N(atLimitKey))));
        var ok = (await Get(keyT, Item(("pk", S("k")), ("sk", N(atLimitKey))))).Item ?? [];
        Ensure(Py.Equal(ok.GetValueOrDefault("sk"), N(atLimitKey)), $"a 38-digit N key value must still round-trip, got {Py.Repr(ok.GetValueOrDefault("sk"))}");
    }
}

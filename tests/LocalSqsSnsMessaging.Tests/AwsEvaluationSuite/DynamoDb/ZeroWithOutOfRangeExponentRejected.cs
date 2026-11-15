// Translated from behavioral/dynamodb/check_zero_with_out_of_range_exponent_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.zero-with-out-of-range-exponent-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ZeroWithOutOfRangeExponentRejected
{
    private const string Check = "zero-with-out-of-range-exponent-rejected";
    private const string ErrValidation = "ValidationException";

    private const string OverflowMsg =
        "1 validation error detected: Number overflow. Attempting to store a number with "
        + "magnitude larger than supported range";

    private const string UnderflowMsg =
        "1 validation error detected: Number underflow. Attempting to store a number with "
        + "magnitude smaller than supported range";

    // Exponents on either side of Long.MAX_VALUE (9223372036854775807): one still parses as an
    // integer and is then range-checked, the other overflows the parse itself.
    private const string ExponentAtLongMax = "9223372036854775807";
    private const string ExponentPastLongMax = "9223372036854775808";

    private static readonly string[] Accepted = ["0E125", "0E-130", "0E5"];

    /// <summary>
    /// A zero written with an out-of-range exponent is ValidationException even though its
    /// value is zero, while an in-range exponent is accepted and canonicalised to "0"; an
    /// unparseable exponent gets a different message than one that merely overflows.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.zero-with-out-of-range-exponent-rejected")]
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
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 30);

        // --- positive control: zero with an IN-RANGE exponent is accepted and canonicalised ---
        foreach (var raw in Accepted)
        {
            var key = $"ok-{raw}";
            await ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = Item(("pk", S(key)), ("v", N(raw))) });
            var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(key))), ConsistentRead = true })).Item;
            Ensure(Py.Equal(got["v"], N("0")), $"{Py.Repr(raw)} is mathematically zero and must canonicalise to \"0\", got {Py.Repr(got["v"])}");
            Ensure(decimal.Parse(got["v"].N, NumberStyles.Float, CultureInfo.InvariantCulture) == 0, $"{Py.Repr(raw)} must round-trip as zero, got {Py.Repr(got["v"])}");
        }

        // --- zero with an exponent past the documented ceiling is ValidationException: overflow ---
        var (code, message) = await Error(() => ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = Item(("pk", S("over")), ("v", N("0E+300"))) }));
        Ensure(code == ErrValidation, $"0E+300 must be rejected with {ErrValidation}, got {code}");
        Ensure(message == OverflowMsg, $"expected AWS's exact overflow message, got {Py.Repr(message)}");
        var leaked = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("over"))), ConsistentRead = true });
        Ensure(leaked.Item is null, $"rejected write must not create the item, found {Py.Repr(leaked.Item)}");

        // --- zero with an exponent past the documented floor is ValidationException: underflow ---
        (code, message) = await Error(() => ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = Item(("pk", S("under")), ("v", N("0E-300"))) }));
        Ensure(code == ErrValidation, $"0E-300 must be rejected with {ErrValidation}, got {code}");
        Ensure(message == UnderflowMsg, $"expected AWS's exact underflow message, got {Py.Repr(message)}");
        leaked = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("under"))), ConsistentRead = true });
        Ensure(leaked.Item is null, $"rejected write must not create the item, found {Py.Repr(leaked.Item)}");

        // --- an exponent that still parses as an integer (Long.MAX_VALUE) is range-checked and
        // overflows with the same message as any other out-of-range magnitude ---
        var atMaxExp = $"1E{ExponentAtLongMax}";
        (code, message) = await Error(() => ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = Item(("pk", S("exp-at-max")), ("v", N(atMaxExp))) }));
        Ensure(code == ErrValidation, $"{Py.Repr(atMaxExp)} must be rejected with {ErrValidation}, got {code}");
        Ensure(message == OverflowMsg, $"expected AWS's exact overflow message, got {Py.Repr(message)}");

        // --- an exponent one past that integer's range cannot even be parsed, and gets the
        // "cannot be converted" message instead of the overflow message ---
        var pastMaxExp = $"1E{ExponentPastLongMax}";
        (code, message) = await Error(() => ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = Item(("pk", S("exp-past-max")), ("v", N(pastMaxExp))) }));
        Ensure(code == ErrValidation, $"{Py.Repr(pastMaxExp)} must be rejected with {ErrValidation}, got {code}");
        Ensure(
            message == $"1 validation error detected: The parameter cannot be converted to a numeric value: {pastMaxExp}",
            $"expected AWS's exact message, got {Py.Repr(message)}");

        // Control still works after every rejection.
        await ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = Item(("pk", S("ctrl")), ("v", N("42"))) });
        var ok = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("ctrl"))), ConsistentRead = true })).Item;
        Ensure(Py.Equal(ok["v"], N("42")), $"control write must still succeed, got {Py.Repr(ok)}");
    }

    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be rejected, but it SUCCEEDED and returned {result}");
        }

        return (error.Code, error.Message);
    }
}

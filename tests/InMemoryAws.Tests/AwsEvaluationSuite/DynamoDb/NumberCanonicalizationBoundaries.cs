// Translated from behavioral/dynamodb/check_number_canonicalization_boundaries.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.number-canonicalization-boundaries</summary>
[Category("AwsEvaluationSuite")]
public sealed class NumberCanonicalizationBoundaries
{
    private const string CheckId = "number-canonicalization-boundaries";
    private const string ErrValidation = "ValidationException";
    private const string OverflowMessage = "1 validation error detected: Number overflow. Attempting to store a number with magnitude larger than supported range";

    // Documented DynamoDB Number range: 1E-130 .. 9.9999999999999999999999999999999999999E+125
    // (and the mirrored negative interval). 38 digits of significand precision.
    private const string MaxMagnitude = "9.9999999999999999999999999999999999999E+125";
    private const string OverMagnitude = "1E+126";
    private const string ThirtyEightDigits = "1.2345678901234567890123456789012345678";

    // Accepted input form -> the canonical decimal STRING DynamoDB stores it as. A leading '+'
    // on the mantissa is accepted and dropped; a bare leading or trailing '.' is accepted and
    // filled; '-0' normalises to '0'; zero padding is stripped; exponents are folded out.
    private static readonly (string Raw, string Expected)[] CanonicalForms =
    [
        ("+7", "7"),
        ("+1.5", "1.5"),
        ("+0", "0"),
        ("-0", "0"),
        ("+0.0", "0"),
        ("+1e2", "100"),
        ("1e+2", "100"),
        ("1.5E+3", "1500"),
        ("+.5", "0.5"),
        (".5", "0.5"),
        ("5.", "5"),
        ("1.e4", "10000"),
        ("00042", "42"),
        ("+1e-2", "0.01"),
        ("1.23E10", "12300000000"),
    ];

    // Malformed mantissa/exponent forms, and any surrounding or embedded whitespace. Each is
    // rejected: N is a string on the wire but a decimal in the data model, so the parse is
    // strict and whitespace is never trimmed for the caller.
    private static readonly string[] MalformedForms =
    [
        "e2", "+e2", "1e", "1e+", "++7", "+-7", "-+7",
        "1+2", "+1+2", "1.2.3", "+1.2.3", "1.2e3.4",
        ".", "+", "-", "0x5", "NaN", "Infinity", "1_000",
        " 7", "7 ", "1 5", "1e1.5",
    ];

    // A mantissa of zero does not exempt the magnitude check: the exponent alone still trips the
    // documented overflow/underflow boundary, with the matching overflow/underflow message.
    private const string ZeroOver = "0E+300";
    private const string ZeroUnder = "0E-300";

    /// <summary>Python's <c>Decimal(a) == Decimal(b)</c> for plain decimal strings, exactly (no 28-digit limit).</summary>
    private static bool DecimalEquals(string a, string b) => Normalize(a) == Normalize(b);

    private static (bool Negative, string Digits, int Exponent) Normalize(string text)
    {
        var negative = text.StartsWith('-');
        text = text.TrimStart('-', '+');
        var exponent = 0;
        var e = text.IndexOfAny(['e', 'E']);
        if (e >= 0)
        {
            exponent = int.Parse(text[(e + 1)..], CultureInfo.InvariantCulture);
            text = text[..e];
        }

        var dot = text.IndexOf('.', StringComparison.Ordinal);
        if (dot >= 0)
        {
            exponent -= text.Length - dot - 1;
            text = text.Remove(dot, 1);
        }

        text = text.TrimStart('0');
        while (text.EndsWith('0'))
        {
            text = text[..^1];
            exponent++;
        }

        return text.Length == 0 ? (false, "", 0) : (negative, text, exponent);
    }

    private static async Task<(string? Code, string? Message)> ErrorCode<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be rejected, but it SUCCEEDED and returned {Py.Repr(result)}");
    }

    /// <summary>
    /// DynamoDB stores N values canonically (zeros trimmed, exponents folded, 38 digits kept) and reads them back by
    /// any spelling; a non-numeric string, magnitudes above 9.99...E+125 and an exponent below the 1E-130 floor raise
    /// ValidationException with the exact message and store nothing, while the 9.99...E+125 ceiling is accepted.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.number-canonicalization-boundaries")]
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

        async Task<GetItemResponse> Get(string pk) =>
            await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S(pk))), ConsistentRead = true });

        // --- canonicalization: leading and trailing zeros are trimmed ---
        await ddb.PutItemAsync(t, Item(
            ("pk", S("canon")),
            ("padded", N("0100.50")),
            ("neg_padded", N("-0.500")),
            ("zero", N("0")),
            ("exp", N("1e3")),
            ("precise", N(ThirtyEightDigits))));
        var item = (await Get("canon")).Item;

        Ensure(
            Py.Equal(item["padded"], N("100.5")),
            $"0100.50 must be stored canonically as 100.5 (leading and trailing zeros trimmed), got {Py.Repr(item["padded"])}");
        Ensure(Py.Equal(item["neg_padded"], N("-0.5")), $"-0.500 must be stored canonically as -0.5, got {Py.Repr(item["neg_padded"])}");
        Ensure(Py.Equal(item["zero"], N("0")), $"0 must round-trip as 0, got {Py.Repr(item["zero"])}");
        // Exponent notation is folded into a plain decimal STRING, not merely into an equal
        // value. The stored form is what a client reads back and compares, so this asserts the
        // string: a value-only comparison (Decimal(..) == Decimal('1000')) is satisfied by an
        // implementation that echoes '1e3' straight back, which is exactly the echoing behaviour this
        // check exists to catch.
        Ensure(Py.Equal(item["exp"], N("1000")), $"exponent notation 1e3 must be stored as the canonical decimal string \"1000\", got {Py.Repr(item["exp"])}");
        Ensure(
            DecimalEquals(item["precise"].N, ThirtyEightDigits),
            $"38 significant digits must round-trip without loss of precision, got {Py.Repr(item["precise"])}");

        // --- the full accepted-form table, asserted on the stored STRING ---
        // One representative value cannot separate "canonicalises" from "happens to echo a form
        // that was already canonical". Each form below differs from its canonical string in a
        // DIFFERENT way (sign prefix, bare leading/trailing dot, zero padding, exponent), so a
        // partial implementation fails on the specific form it does not handle.
        foreach (var (raw, expected) in CanonicalForms)
        {
            var key = $"n-canon-{raw}";
            await ddb.PutItemAsync(t, Item(("pk", S(key)), ("v", N(raw))));
            var got = (await Get(key)).Item["v"];
            Ensure(Py.Equal(got, N(expected)), $"N input {Py.Repr(raw)} must be stored as the canonical string {Py.Repr(expected)}, got {Py.Repr(got)}");
        }

        // --- malformed mantissa/exponent forms and ANY whitespace are rejected ---
        foreach (var raw in MalformedForms)
        {
            var (code, message) = await ErrorCode(() => ddb.PutItemAsync(t, Item(("pk", S("n-malformed")), ("v", N(raw)))));
            Ensure(code == ErrValidation, $"malformed N input {Py.Repr(raw)} must be rejected with {ErrValidation}; got {code}");
            Ensure(
                message == $"1 validation error detected: The parameter cannot be converted to a numeric value: {raw}",
                $"expected AWS's exact ValidationException message, got {Py.Repr(message)}");
        }

        // Effect absent: not one of the rejected writes created the item.
        var leakedMalformed = await Get("n-malformed");
        Ensure(leakedMalformed.Item is null, $"no rejected malformed-N write may create the item, found {Py.Repr(leakedMalformed.Item)}");

        // --- canonical form is what key/comparison paths see ---
        await ddb.PutItemAsync(t, Item(("pk", S("cmp")), ("v", N("01.10"))));
        var matched = (await Get("cmp")).Item;
        Ensure(Py.Equal(matched["v"], N("1.1")), $"01.10 must canonicalize to 1.1, got {Py.Repr(matched["v"])}");
        // A ConditionExpression written against the CANONICAL value must match.
        await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = t,
            Item = Item(("pk", S("cmp")), ("v", N("2"))),
            ConditionExpression = "v = :old",
            ExpressionAttributeValues = Item((":old", N("1.1"))),
        });

        // --- boundary control: the maximum documented magnitude is ACCEPTED ---
        await ddb.PutItemAsync(t, Item(("pk", S("n-max")), ("v", N(MaxMagnitude))));
        var atMax = (await Get("n-max")).Item;
        Ensure(DecimalEquals(atMax["v"].N, MaxMagnitude), $"the documented maximum N magnitude must be accepted and round-trip, got {Py.Repr(atMax["v"])}");

        // --- rejection: one magnitude step past the ceiling ---
        {
            var (code, message) = await ErrorCode(() => ddb.PutItemAsync(t, Item(("pk", S("n-oob")), ("v", N(OverMagnitude)))));
            Ensure(code == ErrValidation, $"N magnitude {OverMagnitude} is past the documented ceiling and must be rejected with {ErrValidation}; got {code}");
            Ensure(message == OverflowMessage, $"expected AWS's exact ValidationException message, got {Py.Repr(message)}");
        }

        // Effect absent: the rejected write created nothing.
        var leaked = await Get("n-oob");
        Ensure(leaked.Item is null, $"a rejected out-of-range PutItem must not create the item, found {Py.Repr(leaked.Item)}");
        // The negative mirror of the range is enforced too.
        var (negCode, negMessage) = await ErrorCode(() => ddb.PutItemAsync(t, Item(("pk", S("n-oob-neg")), ("v", N("-" + OverMagnitude)))));
        Ensure(negCode == ErrValidation, $"N magnitude -{OverMagnitude} must also be rejected with {ErrValidation}; got {negCode}");
        Ensure(negMessage == OverflowMessage, $"expected AWS's exact ValidationException message, got {Py.Repr(negMessage)}");

        // --- a zero mantissa does not exempt the magnitude check: the exponent alone trips it ---
        var (overCode, overMessage) = await ErrorCode(() => ddb.PutItemAsync(t, Item(("pk", S("n-zero-over")), ("v", N(ZeroOver)))));
        Ensure(overCode == ErrValidation, $"{ZeroOver} has a zero mantissa but an out-of-range exponent and must be rejected with {ErrValidation}; got {overCode}");
        Ensure(overMessage == OverflowMessage, $"expected AWS's exact ValidationException message, got {Py.Repr(overMessage)}");
        var leakedZeroOver = await Get("n-zero-over");
        Ensure(leakedZeroOver.Item is null, $"a rejected {ZeroOver} PutItem must not create the item, found {Py.Repr(leakedZeroOver.Item)}");

        var (underCode, underMessage) = await ErrorCode(() => ddb.PutItemAsync(t, Item(("pk", S("n-zero-under")), ("v", N(ZeroUnder)))));
        Ensure(underCode == ErrValidation, $"{ZeroUnder} has a zero mantissa but an out-of-range exponent and must be rejected with {ErrValidation}; got {underCode}");
        Ensure(
            underMessage == "1 validation error detected: Number underflow. Attempting to store a number with magnitude smaller than supported range",
            $"expected AWS's exact ValidationException message, got {Py.Repr(underMessage)}");
        var leakedZeroUnder = await Get("n-zero-under");
        Ensure(leakedZeroUnder.Item is null, $"a rejected {ZeroUnder} PutItem must not create the item, found {Py.Repr(leakedZeroUnder.Item)}");

        // Control still works after the rejections.
        await ddb.PutItemAsync(t, Item(("pk", S("n-ok")), ("v", N("42"))));
        var ok = (await Get("n-ok")).Item;
        Ensure(Py.Equal(ok["v"], N("42")), $"control write must still succeed, got {Py.Repr(ok)}");
    }
}

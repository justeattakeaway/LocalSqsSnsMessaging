// Translated from behavioral/dynamodb/check_number_precision_overflow_in_exponent_form_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.number-precision-overflow-in-exponent-form-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class NumberPrecisionOverflowInExponentFormRejected
{
    private const string CheckId = "number-precision-overflow-in-exponent-form-rejected";
    private const string ErrValidation = "ValidationException";
    private const string PrecisionMessage = "1 validation error detected: Attempting to store more than 38 significant digits in a Number";

    // Both mantissas sit near the top of the magnitude range (exponent E124, one step below the
    // documented ceiling's E125); only their significant-digit count differs.
    private static readonly string OkMantissa = "9." + new string('9', 37); // 38 significant digits: at the documented precision limit
    private static readonly string OverMantissa = "9." + new string('9', 38); // 39 significant digits: one past the limit
    private static readonly string OkInput = OkMantissa + "E124";
    private static readonly string OverInput = OverMantissa + "E124";
    private static readonly string ExpectedOk = new string('9', 38) + new string('0', 125 - 38);

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
    /// A Number written in exponent form is checked for 38-significant-digit precision after
    /// the exponent is folded: 39 significant digits is rejected even though the magnitude is
    /// well inside the documented range, and 38 at the same magnitude is accepted and stored.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.number-precision-overflow-in-exponent-form-rejected")]
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

        // --- positive control: 38 significant digits in exponent form is accepted and expands ---
        await ddb.PutItemAsync(t, Item(("pk", S("ok")), ("v", N(OkInput))));
        var okItem = (await Get("ok")).Item;
        Ensure(
            Py.Equal(okItem["v"], N(ExpectedOk)),
            $"{Py.Repr(OkInput)} (38 significant digits) must be accepted and stored as the expanded plain-decimal string {Py.Repr(ExpectedOk)}, got {Py.Repr(okItem["v"])}");

        // --- 39 significant digits in exponent form is rejected with the 38-digit precision message ---
        var (code, message) = await ErrorCode(() => ddb.PutItemAsync(t, Item(("pk", S("over")), ("v", N(OverInput)))));
        Ensure(code == ErrValidation, $"{Py.Repr(OverInput)} (39 significant digits) must be rejected with {ErrValidation}; got {code}");
        Ensure(message == PrecisionMessage, $"expected AWS's exact ValidationException message, got {Py.Repr(message)}");
        var leaked = await Get("over");
        Ensure(leaked.Item is null, $"a rejected precision-overflow PutItem must not create the item, found {Py.Repr(leaked.Item)}");

        // --- same precision overflow inside an NS member, in exponent form ---
        (code, message) = await ErrorCode(() => ddb.PutItemAsync(t, Item(("pk", S("over-ns")), ("ns", NS(OkInput, OverInput)))));
        Ensure(code == ErrValidation, $"a 39-significant-digit NS member in exponent form must be {ErrValidation}; got {code}");
        Ensure(message == PrecisionMessage, $"expected AWS's exact ValidationException message, got {Py.Repr(message)}");
        var leakedNs = await Get("over-ns");
        Ensure(leakedNs.Item is null, $"a rejected NS write must not create the item, found {Py.Repr(leakedNs.Item)}");

        // Control still works after the rejections.
        await ddb.PutItemAsync(t, Item(("pk", S("control")), ("v", N("42"))));
        var control = (await Get("control")).Item;
        Ensure(Py.Equal(control["v"], N("42")), $"control write must still succeed, got {Py.Repr(control)}");
    }
}

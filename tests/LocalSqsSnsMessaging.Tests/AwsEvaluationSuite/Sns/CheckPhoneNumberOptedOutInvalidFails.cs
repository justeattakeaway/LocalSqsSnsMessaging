// Translated from behavioral/sns/check_check_phone_number_opted_out_invalid_fails.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.check-phone-number-opted-out-invalid-fails</summary>
[Category("AwsEvaluationSuite")]
public sealed class CheckPhoneNumberOptedOutInvalidFails
{
    /// <summary>
    /// CheckIfPhoneNumberIsOptedOut rejects a malformed phone number with InvalidParameter and
    /// the exact message, while a well-formed number returns isOptedOut (positive control).
    /// </summary>
    [Test]
    [DisplayName("sns.check-phone-number-opted-out-invalid-fails")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var suffix = Random.Shared.Next(10000);
        var controlNumber = $"+1500555{suffix:D4}";
        var resp = await sns.CheckIfPhoneNumberIsOptedOutAsync(new CheckIfPhoneNumberIsOptedOutRequest { PhoneNumber = controlNumber });
        Ensure(resp.IsOptedOut is not null, $"positive control did not return isOptedOut: {resp}");

        var (code, message) = await Error(() =>
            sns.CheckIfPhoneNumberIsOptedOutAsync(new CheckIfPhoneNumberIsOptedOutRequest { PhoneNumber = "not-a-phone" }));
        Ensure(code == "InvalidParameter", $"expected InvalidParameter, got {Py.Repr(code)}");
        Ensure(message == "Invalid parameter: PhoneNumber Reason: input incorrectly formatted",
            $"unexpected InvalidParameter message for a malformed phone number: {Py.Repr(message)}");
    }

    /// <summary>
    /// Call fn(); return botocore (Error.Code, Error.Message) of its ClientError.
    /// An unexpected success raises.
    /// </summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error is null
            ? throw Fail($"expected rejection, but the call SUCCEEDED and returned {result}")
            : (error.Code, error.Message);
    }
}

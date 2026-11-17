// Translated from behavioral/sns/check_opt_in_phone_number_invalid_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.opt-in-phone-number-invalid-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class OptInPhoneNumberInvalidRejected
{
    /// <summary>OptInPhoneNumber must reject a phone number that is not in E.164 format.</summary>
    [Test]
    [DisplayName("sns.opt-in-phone-number-invalid-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var suffix = Random.Shared.Next(10000);
        var validNumber = $"+1500555{suffix:D4}";

        await sns.OptInPhoneNumberAsync(new OptInPhoneNumberRequest { PhoneNumber = validNumber });
        var resp = await sns.CheckIfPhoneNumberIsOptedOutAsync(new CheckIfPhoneNumberIsOptedOutRequest { PhoneNumber = validNumber });
        Ensure(resp.IsOptedOut == false,
            $"OptInPhoneNumber on a valid number must leave it opted in, got {resp.IsOptedOut}");

        var (code, message) = await Error(() => sns.OptInPhoneNumberAsync(new OptInPhoneNumberRequest { PhoneNumber = "not-a-phone-number" }));
        Ensure(code == "InvalidParameter",
            $"expected InvalidParameter for a malformed phone number, got {code}");
        const string expected = "Invalid parameter: PhoneNumber Reason: input incorrectly formatted";
        Ensure(message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(message)}");

        var optedOut = new HashSet<string>(StringComparer.Ordinal);
        string? token = null;
        do
        {
            var page = await sns.ListPhoneNumbersOptedOutAsync(new ListPhoneNumbersOptedOutRequest { NextToken = token });
            optedOut.UnionWith(page.PhoneNumbers ?? []);
            token = page.NextToken;
        }
        while (!string.IsNullOrEmpty(token));

        Ensure(!optedOut.Contains("not-a-phone-number"),
            $"rejected malformed number must not appear in opt-out state: {Py.Repr(optedOut)}");
    }

    /// <summary>
    /// Call fn(); return (code, message) of the ClientError it raises. An unexpected success
    /// raises, so a target that accepts what AWS refuses never reads as a code mismatch.
    /// </summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error is null
            ? throw Fail($"expected the call to be refused, but it succeeded and returned {result}")
            : (error.Code, error.Message);
    }
}

// Translated from behavioral/sns/check_create_sms_sandbox_unsupported_country_code_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.RegularExpressions;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.create-sms-sandbox-unsupported-country-code-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed partial class CreateSmsSandboxUnsupportedCountryCodeRejected
{
    // AWS appends the request id of the inner service call to this message; it is the only part
    // that varies per call.
    [GeneratedRegex("Request ID: [0-9A-Za-z-]+")]
    private static partial Regex RequestIdPattern();

    private static string? Stable(string? message) =>
        message is null ? null : RequestIdPattern().Replace(message, "Request ID: <request-id>");

    // "+0..." satisfies the API's pattern (^(\+[0-9]{8,}|[0-9]{0,9})$), but no E.164 calling
    // code starts with 0: well-formed, yet an unsupported country code.
    private const string UnsupportedCountryNumber = "+0123456789";

    // A UK number, not NANP (+1): NANP destinations need a registered origination identity
    // (10DLC/toll-free) even for the sandbox OTP, else an unrelated UserError. Ofcom reserves
    // 07700 900xxx for fictional use, so it is never a routable subscriber.
    private const string ValidNumber = "+447700900125";

    /// <summary>
    /// CreateSMSSandboxPhoneNumber refuses a number with an unassigned country code with
    /// InvalidParameter and does not list it; a real-country number never fails InvalidParameter.
    /// </summary>
    [Test]
    [DisplayName("sns.create-sms-sandbox-unsupported-country-code-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;

        var (code, message) = await RejectedError(
            () => sns.CreateSMSSandboxPhoneNumberAsync(new CreateSMSSandboxPhoneNumberRequest { PhoneNumber = UnsupportedCountryNumber }),
            description: $"a well-formed but unsupported-country-code number ({Py.Repr(UnsupportedCountryNumber)})");
        Ensure(code == "InvalidParameter",
            "expected InvalidParameter for unsupported-country-code number " +
            $"{Py.Repr(UnsupportedCountryNumber)}, got {Py.Repr(code)}");
        // The message relays the internal downstream call, whose request id is the only varying part.
        const string expected =
            "Invalid parameter: The following parameter is null, empty or invalid: " +
            "['destinationPhoneNumber'] (Service: PinpointSmsVoiceV2, Status Code: 400, " +
            "Request ID: <request-id>)";
        Ensure(Stable(message) == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(message)}");
        Ensure(!await Contains(sns, UnsupportedCountryNumber, timeoutS: 10.0),
            $"rejected number {Py.Repr(UnsupportedCountryNumber)} must not appear in " +
            "list_sms_sandbox_phone_numbers, but it does");

        // Succeeds, or fails later for a different reason when the account has no SMS
        // origination identity; both outcomes are legitimate.
        var validError = await FidelityContext.CaptureError(() =>
            sns.CreateSMSSandboxPhoneNumberAsync(new CreateSMSSandboxPhoneNumberRequest { PhoneNumber = ValidNumber }));
        var codeValid = validError?.Code;
        if (validError is null)
        {
            ctx.OnCleanup(() => sns.DeleteSMSSandboxPhoneNumberAsync(new DeleteSMSSandboxPhoneNumberRequest { PhoneNumber = ValidNumber }));
            Ensure(await Contains(sns, ValidNumber, timeoutS: 20.0),
                $"accepted control number {Py.Repr(ValidNumber)} did not appear in " +
                "list_sms_sandbox_phone_numbers within 20s");
        }
        else
        {
            Ensure(codeValid != "InvalidParameter",
                "a valid supported-country number must not fail the country-code check " +
                $"(same InvalidParameter as the unsupported-country-code case); got {Py.Repr(codeValid)}");
        }
    }

    /// <summary>(Code, Message) of a call that is always invalid; an unexpected success raises.</summary>
    private static async Task<(string? Code, string? Message)> RejectedError<T>(Func<Task<T>> fn, string description)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error is null
            ? throw Fail($"expected {description} to be rejected, but it SUCCEEDED and returned {result}")
            : (error.Code, error.Message);
    }

    private static async Task<bool> Contains(AmazonSimpleNotificationServiceClient sns, string phoneNumber, double timeoutS = 10.0, double intervalS = 1.0)
    {
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            string? token = null;
            while (true)
            {
                var page = await sns.ListSMSSandboxPhoneNumbersAsync(new ListSMSSandboxPhoneNumbersRequest { NextToken = token });
                if ((page.PhoneNumbers ?? []).Any(entry => entry.PhoneNumber == phoneNumber))
                {
                    return true;
                }

                token = page.NextToken;
                if (string.IsNullOrEmpty(token))
                {
                    break;
                }
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        return false;
    }
}

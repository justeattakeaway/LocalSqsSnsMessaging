// Translated from behavioral/sns/check_create_sms_sandbox_invalid_number_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.create-sms-sandbox-invalid-number-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateSmsSandboxInvalidNumberRejected
{
    // A UK number, not a NANP (+1) number: NANP destinations now require a registered
    // origination identity (10DLC/toll-free) to send even the sandbox OTP, which the
    // verification account may not have, and that would mask the malformed-number
    // boundary under test with an unrelated "no origination entities available" UserError.
    // The 07700 900xxx range is reserved by Ofcom for fictional use, so it's never a
    // routable subscriber -- safe as a real, supported-number positive control.
    private const string ValidNumber = "+447700900124";

    private const string InvalidNumberMessage =
        "Invalid parameter: Validation Error Occurred - Reason=\"INVALID_PARAMETER\" " +
        "Fields=\"destinationPhoneNumber\" (Service: PinpointSmsVoiceV2, Status Code: 400";

    /// <summary>CreateSMSSandboxPhoneNumber must reject a phone number that is not in E.164 format.</summary>
    [Test]
    [DisplayName("sns.create-sms-sandbox-invalid-number-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;

        var (result, error) = await FidelityContext.Capture(() =>
            sns.CreateSMSSandboxPhoneNumberAsync(new CreateSMSSandboxPhoneNumberRequest { PhoneNumber = "12345" }));
        if (error is null)
        {
            throw Fail($"expected a rejection for a non-E.164 sandbox phone number, but the call SUCCEEDED: {result}");
        }

        var (code, message) = (error.Code, error.Message);
        Ensure(code == "InvalidParameter",
            $"expected InvalidParameter for a non-E.164 sandbox phone number, got {code}");
        // The message ends with the request id of SNS's downstream SMS call, which differs
        // from the SNS RequestId and from run to run; everything before it is asserted whole.
        const string sep = ", Request ID: ";
        var at = (message ?? "").LastIndexOf(sep, StringComparison.Ordinal);
        var requestId = at < 0 ? "" : message![(at + sep.Length)..];
        Ensure(at >= 0 && requestId.Length == 37 && requestId.EndsWith(')'),
            $"unexpected request id suffix for a non-E.164 sandbox phone number: {Py.Repr(message)}");
        Ensure(message == $"{InvalidNumberMessage}, Request ID: {requestId}",
            $"unexpected message for a non-E.164 sandbox phone number: {Py.Repr(message)}");

        var listed = ((await sns.ListSMSSandboxPhoneNumbersAsync(new ListSMSSandboxPhoneNumbersRequest())).PhoneNumbers ?? [])
            .Select(n => n.PhoneNumber).ToHashSet(StringComparer.Ordinal);
        Ensure(!listed.Contains("12345"), $"rejected sandbox number must not be persisted: {Py.Repr(listed)}");

        // Positive control: without this, the InvalidParameter above cannot be told
        // apart from CreateSMSSandboxPhoneNumber rejecting every call (no permission,
        // no origination entity, a broken target). A well-formed number must either
        // succeed (verified Pending) or fail for a DISTINCT, non-InvalidParameter
        // reason -- proving the malformed-number rejection is a specific, earlier
        // validation layer rather than a blanket failure.
        var validError = await FidelityContext.CaptureError(() =>
            sns.CreateSMSSandboxPhoneNumberAsync(new CreateSMSSandboxPhoneNumberRequest { PhoneNumber = ValidNumber }));
        var codeValid = validError?.Code;
        if (validError is null)
        {
            ctx.OnCleanup(() => sns.DeleteSMSSandboxPhoneNumberAsync(new DeleteSMSSandboxPhoneNumberRequest { PhoneNumber = ValidNumber }));
            var listedAfter = await PollListed(ctx, sns, ValidNumber, capS: 20.0);
            Ensure(listedAfter.GetValueOrDefault(ValidNumber) == "Pending",
                $"valid sandbox number missing/not Pending after creation: {Py.Repr(listedAfter)}");
        }
        else
        {
            Ensure(codeValid != "InvalidParameter",
                $"a well-formed number must not fail with the same code as the malformed one, got {codeValid}");
        }
    }

    /// <summary>A poll's timeout: cap_s, bounded by the runner's remaining time less 5 s.</summary>
    private static double BudgetS(FidelityContext ctx, double capS) =>
        Math.Max(1.0, Math.Min(capS, ctx.TimeLeftSeconds - 5.0));

    /// <summary>Every listed sandbox number with its Status, re-read until phone_number appears or the cap ends.</summary>
    private static async Task<Dictionary<string, string?>> PollListed(FidelityContext ctx, AmazonSimpleNotificationServiceClient sns, string phoneNumber, double capS)
    {
        var deadline = FidelityTime.StartNew();
        var budget = TimeSpan.FromSeconds(BudgetS(ctx, capS));
        while (true)
        {
            var listed = new Dictionary<string, string?>(StringComparer.Ordinal);
            string? token = null;
            while (true)
            {
                var page = await sns.ListSMSSandboxPhoneNumbersAsync(new ListSMSSandboxPhoneNumbersRequest { NextToken = token });
                foreach (var n in page.PhoneNumbers ?? [])
                {
                    listed[n.PhoneNumber] = n.Status?.Value;
                }

                token = page.NextToken;
                if (string.IsNullOrEmpty(token))
                {
                    break;
                }
            }

            if (listed.ContainsKey(phoneNumber) || deadline.Elapsed >= budget)
            {
                return listed;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1.0));
        }
    }
}

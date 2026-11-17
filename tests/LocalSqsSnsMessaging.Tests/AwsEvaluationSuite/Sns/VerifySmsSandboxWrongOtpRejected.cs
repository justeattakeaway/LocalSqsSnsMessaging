// Translated from behavioral/sns/check_verify_sms_sandbox_wrong_otp_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.verify-sms-sandbox-wrong-otp-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class VerifySmsSandboxWrongOtpRejected
{
    // A UK number, not a NANP (+1) number: NANP destinations now require a registered
    // origination identity (10DLC/toll-free) to send even the sandbox OTP. The 07700
    // 900xxx range is reserved by Ofcom for fictional/dramatic use, so it's syntactically
    // valid but never a routable subscriber -- it can only ever be verified with a wrong
    // code, which is exactly what we want.
    //
    // The origination-identity gate is not NANP-specific: on an account with no
    // origination identity, CreateSMSSandboxPhoneNumber raises UserError ("No
    // origination entities available to send") for this UK number too. That failure is
    // send-only: the number is still registered server-side as Pending despite the
    // raised UserError. So the UserError is tolerated here rather than treated as fatal, and Pending is
    // confirmed independently via list, which is what the rest of this check depends on.
    // Both numbers are drawn per run from that range, so concurrent runs in one account never share
    // a sandbox entry.
    private const string ReservedPrefix = "+447700900";

    /// <summary>
    /// Verifying a pending SMS sandbox phone number with an incorrect OTP is rejected
    /// with VerificationException, leaves the number's status at Pending (not Verified),
    /// and is a distinct error class from verifying a number that was never added.
    /// </summary>
    [Test]
    [DisplayName("sns.verify-sms-sandbox-wrong-otp-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var pendingSuffix = Random.Shared.Next(1000);
        int neverAddedSuffix;
        do
        {
            neverAddedSuffix = Random.Shared.Next(1000);
        }
        while (neverAddedSuffix == pendingSuffix);

        var pendingNumber = $"{ReservedPrefix}{pendingSuffix:D3}";
        var neverAddedNumber = $"{ReservedPrefix}{neverAddedSuffix:D3}";
        var sns = ctx.Sns;

        var createError = await FidelityContext.CaptureError(() =>
            sns.CreateSMSSandboxPhoneNumberAsync(new CreateSMSSandboxPhoneNumberRequest { PhoneNumber = pendingNumber }));
        // Without an origination identity the confirmation OTP can't be dispatched,
        // but the number still lands in Pending server-side, so this is not fatal to
        // the boundary under test.
        if (createError is not null && createError.Code != "UserError")
        {
            throw createError.Exception;
        }

        ctx.OnCleanup(() => DeleteIfPresent(sns, pendingNumber));

        // Positive control: the number reached Pending, regardless of whether the create
        // call itself raised.
        var status = await SandboxStatus(sns, pendingNumber);
        Ensure(status == "Pending", $"expected {pendingNumber} to be Pending, got {status}");

        // Break exactly one thing: verify with a wrong OTP.
        var (code, message) = await Error(() => sns.VerifySMSSandboxPhoneNumberAsync(new VerifySMSSandboxPhoneNumberRequest
        {
            PhoneNumber = pendingNumber, OneTimePassword = "000000",
        }));
        Ensure(code == "VerificationException", $"expected VerificationException, got {code}");
        Ensure(message == "Verification code has expired, cannot verify the destination phone number.",
            $"unexpected VerificationException message: {Py.Repr(message)}");

        // Effect absent: the number did not transition to Verified.
        status = await SandboxStatus(sns, pendingNumber);
        Ensure(status == "Pending", $"expected {pendingNumber} to remain Pending, got {status}");

        // Distinct boundary: verifying a number that was never added at all is a different
        // error class (ResourceNotFound), not the same VerificationException.
        var (codeUnknown, messageUnknown) = await Error(() => sns.VerifySMSSandboxPhoneNumberAsync(new VerifySMSSandboxPhoneNumberRequest
        {
            PhoneNumber = neverAddedNumber, OneTimePassword = "000000",
        }));
        Ensure(codeUnknown == "ResourceNotFound", $"expected ResourceNotFound, got {codeUnknown}");
        Ensure(messageUnknown == "Resource does not exist",
            $"unexpected ResourceNotFound message: {Py.Repr(messageUnknown)}");

        // The cleanup delete is already mandatory; pull it into the body so it's a proven
        // negative rather than an unasserted teardown. Deletes prove gone and unusable.
        await sns.DeleteSMSSandboxPhoneNumberAsync(new DeleteSMSSandboxPhoneNumberRequest { PhoneNumber = pendingNumber });
        Ensure(await SandboxAbsent(sns, pendingNumber),
            $"{pendingNumber} must not still appear in the sandbox list after delete");

        var (codeDeleted, messageDeleted) = await Error(() => sns.VerifySMSSandboxPhoneNumberAsync(new VerifySMSSandboxPhoneNumberRequest
        {
            PhoneNumber = pendingNumber, OneTimePassword = "000000",
        }));
        Ensure(codeDeleted == "ResourceNotFound",
            $"expected ResourceNotFound verifying a deleted number, got {codeDeleted}");
        Ensure(messageDeleted == "Resource does not exist",
            $"unexpected ResourceNotFound message: {Py.Repr(messageDeleted)}");
    }

    /// <summary>Call fn(); return the ClientError's (Code, Message). A success raises.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (_, error) = await FidelityContext.Capture(fn);
        return error is null
            ? throw Fail("expected a ClientError, call succeeded")
            : (error.Code, error.Message);
    }

    /// <summary>Teardown: the body deletes the number itself, so only a still-present number is deleted.</summary>
    private static async Task DeleteIfPresent(AmazonSimpleNotificationServiceClient sns, string phoneNumber)
    {
        try
        {
            await sns.DeleteSMSSandboxPhoneNumberAsync(new DeleteSMSSandboxPhoneNumberRequest { PhoneNumber = phoneNumber });
        }
        catch (AmazonSimpleNotificationServiceException ex) when (ex.ErrorCode == "ResourceNotFound")
        {
        }
    }

    private static async Task<string?> SandboxStatus(AmazonSimpleNotificationServiceClient sns, string phoneNumber, double timeoutS = 20.0, double intervalS = 1.0)
    {
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            string? token = null;
            while (true)
            {
                var page = await sns.ListSMSSandboxPhoneNumbersAsync(new ListSMSSandboxPhoneNumbersRequest { NextToken = token });
                foreach (var entry in page.PhoneNumbers ?? [])
                {
                    if (entry.PhoneNumber == phoneNumber)
                    {
                        return entry.Status?.Value;
                    }
                }

                token = page.NextToken;
                if (string.IsNullOrEmpty(token))
                {
                    break;
                }
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"phone number {phoneNumber} never appeared in sandbox list");
    }

    private static async Task<bool> SandboxAbsent(AmazonSimpleNotificationServiceClient sns, string phoneNumber, double timeoutS = 20.0, double intervalS = 1.0)
    {
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            string? token = null;
            var found = false;
            while (true)
            {
                var page = await sns.ListSMSSandboxPhoneNumbersAsync(new ListSMSSandboxPhoneNumbersRequest { NextToken = token });
                if ((page.PhoneNumbers ?? []).Any(entry => entry.PhoneNumber == phoneNumber))
                {
                    found = true;
                    break;
                }

                token = page.NextToken;
                if (string.IsNullOrEmpty(token))
                {
                    break;
                }
            }

            if (!found)
            {
                return true;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"phone number {phoneNumber} still present in sandbox list after delete");
    }
}

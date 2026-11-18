// Translated from behavioral/sns/check_set_sms_attributes_invalid_name_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.set-sms-attributes-invalid-name-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class SetSmsAttributesInvalidNameRejected
{
    /// <summary>SetSMSAttributes must reject unknown attribute names instead of storing them.</summary>
    [Test]
    [DisplayName("sns.set-sms-attributes-invalid-name-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;

        var original = (await sns.GetSMSAttributesAsync(new GetSMSAttributesRequest { Attributes = ["DefaultSenderID"] })).Attributes ?? [];
        var originalValue = original.GetValueOrDefault("DefaultSenderID");
        ctx.OnCleanup(() => sns.SetSMSAttributesAsync(new SetSMSAttributesRequest
        {
            Attributes = new() { ["DefaultSenderID"] = string.IsNullOrEmpty(originalValue) ? "" : originalValue },
        }));

        // Positive control: a genuinely valid attribute name must be accepted and
        // reflected, proving the account/credentials are healthy for
        // SetSMSAttributes before the negative below is meaningful.
        var controlValue = ("S2R" + Guid.NewGuid().ToString("N").ToUpperInvariant())[..11];
        await sns.SetSMSAttributesAsync(new SetSMSAttributesRequest { Attributes = new() { ["DefaultSenderID"] = controlValue } });
        var reflected = (await sns.GetSMSAttributesAsync(new GetSMSAttributesRequest { Attributes = ["DefaultSenderID"] })).Attributes ?? [];
        Ensure(reflected.GetValueOrDefault("DefaultSenderID") == controlValue,
            $"positive control failed: expected DefaultSenderID={Py.Repr(controlValue)}, got {Py.Repr(reflected)}");

        var error = await FidelityContext.CaptureError(() =>
            sns.SetSMSAttributesAsync(new SetSMSAttributesRequest { Attributes = new() { ["NotARealSmsAttribute"] = "some-value" } }));
        if (error is null)
        {
            throw Fail(
                "SetSMSAttributes with an unknown attribute name was expected to be rejected, " +
                "but it SUCCEEDED");
        }

        var (code, message) = (error.Code, error.Message);
        Ensure(code == "InvalidParameter",
            $"expected InvalidParameter for an unknown SMS attribute name, got {code}");
        Ensure(message == "NotARealSmsAttribute is not a valid attribute",
            $"expected the exact message 'NotARealSmsAttribute is not a valid attribute', got {Py.Repr(message)}");

        var attrs = (await sns.GetSMSAttributesAsync(new GetSMSAttributesRequest())).Attributes ?? [];
        Ensure(!attrs.ContainsKey("NotARealSmsAttribute"),
            $"rejected SetSMSAttributes must not persist the attribute: {Py.Repr(attrs)}");
    }
}

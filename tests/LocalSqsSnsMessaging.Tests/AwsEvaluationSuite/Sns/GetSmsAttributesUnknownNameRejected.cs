// Translated from behavioral/sns/check_get_sms_attributes_unknown_name_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.get-sms-attributes-unknown-name-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class GetSmsAttributesUnknownNameRejected
{
    /// <summary>
    /// GetSMSAttributes rejects a filter naming an attribute SNS does not define,
    /// rather than silently answering with that key absent.
    /// </summary>
    [Test]
    [DisplayName("sns.get-sms-attributes-unknown-name-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;

        // Positive control: a real attribute name, filtered, is answered normally. This
        // proves GetSMSAttributes' filter argument works in this account before the
        // negative below is meaningful.
        var control = (await sns.GetSMSAttributesAsync(new GetSMSAttributesRequest { Attributes = ["MonthlySpendLimit"] })).Attributes ?? [];
        Ensure(control.GetValueOrDefault("MonthlySpendLimit") is not null,
            $"positive control failed: expected a MonthlySpendLimit value, got {Py.Repr(control)}");

        var (result, error) = await FidelityContext.Capture(() =>
            sns.GetSMSAttributesAsync(new GetSMSAttributesRequest { Attributes = ["NotARealSmsAttribute"] }));
        if (error is null)
        {
            throw Fail(
                "GetSMSAttributes with an unknown attribute filter name was expected to be " +
                $"rejected, but it SUCCEEDED with {Py.Repr(result!.Attributes)}");
        }

        var (code, message) = (error.Code, error.Message);
        const string expected = "Unknown attribute requested: NotARealSmsAttribute";
        Ensure((code, message) == ("InvalidParameter", expected),
            $"expected InvalidParameter {Py.Repr(expected)} for an unknown SMS attribute filter name, got " +
            $"{Py.Repr(code)} {Py.Repr(message)}");

        // Effect absent: the rejected filter must not have added the unknown name, or
        // disturbed the real attribute it was asked alongside.
        var after = (await sns.GetSMSAttributesAsync(new GetSMSAttributesRequest { Attributes = ["MonthlySpendLimit"] })).Attributes ?? [];
        Ensure(Py.Equal(after, control),
            "a rejected GetSMSAttributes call must not change subsequent reads: " +
            $"before={Py.Repr(control)} after={Py.Repr(after)}");
    }
}

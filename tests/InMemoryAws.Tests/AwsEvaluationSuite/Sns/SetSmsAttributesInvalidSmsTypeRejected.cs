// Translated from behavioral/sns/check_set_sms_attributes_invalid_sms_type_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.set-sms-attributes-invalid-sms-type-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class SetSmsAttributesInvalidSmsTypeRejected
{
    /// <summary>
    /// SetSMSAttributes refuses DefaultSMSType=Marketing (the legal values are Promotional
    /// and Transactional) and the account's DefaultSMSType reads back exactly as it was.
    /// </summary>
    [Test]
    [DisplayName("sns.set-sms-attributes-invalid-sms-type-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;

        var before = (await sns.GetSMSAttributesAsync(new GetSMSAttributesRequest { Attributes = ["DefaultSMSType"] })).Attributes ?? [];
        var (code, message) = await Error(() =>
            sns.SetSMSAttributesAsync(new SetSMSAttributesRequest { Attributes = new() { ["DefaultSMSType"] = "Marketing" } }));
        Ensure(code == "InvalidParameter", $"DefaultSMSType=Marketing gave {Py.Repr(code)}");
        Ensure(message == "DefaultSMSType is invalid", $"unexpected message for DefaultSMSType=Marketing: {Py.Repr(message)}");

        var after = (await sns.GetSMSAttributesAsync(new GetSMSAttributesRequest { Attributes = ["DefaultSMSType"] })).Attributes ?? [];
        Ensure(after.GetValueOrDefault("DefaultSMSType") != "Marketing", "the refused value must not be stored");
        Ensure(Py.Equal(after, before),
            $"the refused SetSMSAttributes must leave DefaultSMSType as it was: before={Py.Repr(before)} after={Py.Repr(after)}");
    }

    /// <summary>Return (Error.Code, Error.Message) of the ClientError fn raises; a success raises.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error is null
            ? throw Fail($"expected the call to be rejected, but it SUCCEEDED and returned {result}")
            : (error.Code, error.Message);
    }
}

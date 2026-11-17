// Translated from behavioral/sns/check_list_sms_sandbox_phone_numbers_max_results_over_limit_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.list-sms-sandbox-phone-numbers-max-results-over-limit-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListSmsSandboxPhoneNumbersMaxResultsOverLimitRejected
{
    // The SDK's Roslyn analyzer refuses the literal 101, so it comes from a field.
    private static readonly int OverLimit = 101;

    /// <summary>
    /// ListSMSSandboxPhoneNumbers refuses MaxResults=101, one past its documented 100 maximum,
    /// while MaxResults=100 and a call without MaxResults are both accepted.
    /// </summary>
    [Test]
    [DisplayName("sns.list-sms-sandbox-phone-numbers-max-results-over-limit-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;

        // SNS answers ValidationError, not the InvalidParameterException the model declares.
        var (code, message) = await Error(() => sns.ListSMSSandboxPhoneNumbersAsync(new ListSMSSandboxPhoneNumbersRequest { MaxResults = OverLimit }));
        Ensure(code == "ValidationError", $"MaxResults=101 gave {Py.Repr(code)}");
        Ensure(message == "1 validation error detected: Value '101' at 'maxResults' failed to satisfy " +
                          "constraint: Member must have value less than or equal to 100", $"unexpected message for MaxResults=101: {Py.Repr(message)}");

        // boto3 always hands back a PhoneNumbers list; the .NET SDK leaves an empty one null.
        var atLimit = await sns.ListSMSSandboxPhoneNumbersAsync(new ListSMSSandboxPhoneNumbersRequest { MaxResults = 100 });
        var atLimitNumbers = atLimit.PhoneNumbers ?? [];
        Ensure(atLimitNumbers.Count <= 100, "a MaxResults=100 page must hold at most 100 numbers");

        var unbounded = await sns.ListSMSSandboxPhoneNumbersAsync(new ListSMSSandboxPhoneNumbersRequest());
        Ensure(unbounded is not null, "a call without MaxResults must be accepted");
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

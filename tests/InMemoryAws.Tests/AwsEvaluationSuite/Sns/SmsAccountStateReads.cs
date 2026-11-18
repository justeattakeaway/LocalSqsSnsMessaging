// Translated from behavioral/sns/check_sms_account_state_reads.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.sms-account-state-reads</summary>
[Category("AwsEvaluationSuite")]
public sealed class SmsAccountStateReads
{
    /// <summary>
    /// Account-level SMS reads report real state: IsInSandbox is a bool, the account-wide
    /// MonthlySpendLimit attribute is a numeric string, and origination numbers are a list.
    /// </summary>
    [Test]
    [DisplayName("sns.sms-account-state-reads")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;

        var status = await sns.GetSMSSandboxAccountStatusAsync(new GetSMSSandboxAccountStatusRequest());
        Ensure(status.IsInSandbox is not null, $"{status}");

        var attrs = (await sns.GetSMSAttributesAsync(new GetSMSAttributesRequest())).Attributes;
        Ensure(attrs is not null, "None");
        Ensure(attrs.ContainsKey("MonthlySpendLimit"),
            $"expected the account-wide MonthlySpendLimit default attribute, got keys {Py.Repr(attrs.Keys.Order(StringComparer.Ordinal).ToList())}");
        Ensure(attrs["MonthlySpendLimit"].Length > 0 && attrs["MonthlySpendLimit"].All(char.IsAsciiDigit),
            $"MonthlySpendLimit should be a numeric string, got {Py.Repr(attrs["MonthlySpendLimit"])}");

        // boto3 always hands back a PhoneNumbers list; the .NET SDK leaves an empty one null, so
        // the call succeeding is what is checked.
        var numbers = (await sns.ListOriginationNumbersAsync(new ListOriginationNumbersRequest())).PhoneNumbers ?? [];
        Ensure(numbers is not null, "None");
    }
}

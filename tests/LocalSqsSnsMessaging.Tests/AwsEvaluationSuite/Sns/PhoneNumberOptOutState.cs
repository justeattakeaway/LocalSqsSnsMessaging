// Translated from behavioral/sns/check_phone_number_opt_out_state.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.phone-number-opt-out-state</summary>
[Category("AwsEvaluationSuite")]
public sealed class PhoneNumberOptOutState
{
    /// <summary>CheckIfPhoneNumberIsOptedOut reports False for an unused number and ListPhoneNumbersOptedOut agrees.</summary>
    [Test]
    [DisplayName("sns.phone-number-opt-out-state")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var suffix = Random.Shared.Next(10000);
        var phoneNumber = $"+1500555{suffix:D4}";

        var resp = await sns.CheckIfPhoneNumberIsOptedOutAsync(new CheckIfPhoneNumberIsOptedOutRequest { PhoneNumber = phoneNumber });
        Ensure(resp.IsOptedOut == false, $"unused number should not be opted out, got {resp.IsOptedOut}");

        var respAgain = await sns.CheckIfPhoneNumberIsOptedOutAsync(new CheckIfPhoneNumberIsOptedOutRequest { PhoneNumber = phoneNumber });
        Ensure(respAgain.IsOptedOut == false,
            $"a read must not flip opt-out state: first call False, second call got {respAgain.IsOptedOut}");

        var optedOut = new HashSet<string>(StringComparer.Ordinal);
        string? token = null;
        do
        {
            var page = await sns.ListPhoneNumbersOptedOutAsync(new ListPhoneNumbersOptedOutRequest { NextToken = token });
            optedOut.UnionWith(page.PhoneNumbers ?? []);
            token = page.NextToken;
        }
        while (!string.IsNullOrEmpty(token));

        Ensure(!optedOut.Contains(phoneNumber), $"{phoneNumber} unexpectedly in opt-out list");
    }
}

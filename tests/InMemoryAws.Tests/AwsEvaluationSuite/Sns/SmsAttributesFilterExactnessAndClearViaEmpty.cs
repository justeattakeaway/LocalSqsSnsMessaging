// Translated from behavioral/sns/check_sms_attributes_filter_exactness_and_clear_via_empty.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.sms-attributes-filter-exactness-and-clear-via-empty</summary>
[Category("AwsEvaluationSuite")]
public sealed class SmsAttributesFilterExactnessAndClearViaEmpty
{
    /// <summary>
    /// GetSMSAttributes filtered to one name returns exactly that key while an unfiltered read has
    /// more, and SetSMSAttributes with an empty value removes the attribute rather than blanking it.
    /// </summary>
    [Test]
    [DisplayName("sns.sms-attributes-filter-exactness-and-clear-via-empty")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;

        var before = (await sns.GetSMSAttributesAsync(new GetSMSAttributesRequest())).Attributes ?? [];
        var originalType = before.GetValueOrDefault("DefaultSMSType");
        ctx.OnCleanup(() => sns.SetSMSAttributesAsync(new SetSMSAttributesRequest
        {
            Attributes = new() { ["DefaultSMSType"] = string.IsNullOrEmpty(originalType) ? "" : originalType },
        }));

        // Real AWS caps MonthlySpendLimit per account (often at "1"), so the filter/clear behavior
        // is exercised on DefaultSMSType, which carries no account-specific quota.
        await sns.SetSMSAttributesAsync(new SetSMSAttributesRequest { Attributes = new() { ["DefaultSMSType"] = "Transactional" } });
        var selected = (await sns.GetSMSAttributesAsync(new GetSMSAttributesRequest { Attributes = ["DefaultSMSType"] })).Attributes ?? [];
        Ensure(selected.Count == 1 && selected.GetValueOrDefault("DefaultSMSType") == "Transactional",
            "GetSMSAttributes(attributes=['DefaultSMSType']) must return EXACTLY that " +
            $"key, got {Py.Repr(selected)}");

        var everything = (await sns.GetSMSAttributesAsync(new GetSMSAttributesRequest())).Attributes ?? [];
        Ensure(everything.GetValueOrDefault("DefaultSMSType") == "Transactional",
            $"expected DefaultSMSType='Transactional' in the unfiltered read, got {Py.Repr(everything)}");
        Ensure(everything.ContainsKey("MonthlySpendLimit"),
            "the unfiltered read must still carry account attributes outside the filter, " +
            $"got {Py.Repr(everything)}");

        await sns.SetSMSAttributesAsync(new SetSMSAttributesRequest { Attributes = new() { ["DefaultSMSType"] = "" } });
        var after = (await sns.GetSMSAttributesAsync(new GetSMSAttributesRequest())).Attributes ?? [];
        Ensure(!after.ContainsKey("DefaultSMSType"),
            "SetSMSAttributes with an empty value must remove DefaultSMSType entirely " +
            $"(not leave it present and blank), got {Py.Repr(after)}");
        Ensure(after.ContainsKey("MonthlySpendLimit"),
            $"clearing DefaultSMSType must not disturb other account attributes, got {Py.Repr(after)}");
    }
}

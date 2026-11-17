// Translated from behavioral/sns/check_sms_attributes_custom_value_round_trip.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.sms-attributes-custom-value-round-trip</summary>
[Category("AwsEvaluationSuite")]
public sealed class SmsAttributesCustomValueRoundTrip
{
    /// <summary>
    /// A custom SMS account attribute value set via SetSMSAttributes is
    /// reflected exactly by a subsequent GetSMSAttributes call.
    /// </summary>
    [Test]
    [DisplayName("sns.sms-attributes-custom-value-round-trip")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;

        var before = (await sns.GetSMSAttributesAsync(new GetSMSAttributesRequest())).Attributes ?? [];
        var originalValue = before.GetValueOrDefault("DefaultSenderID");

        ctx.OnCleanup(() => sns.SetSMSAttributesAsync(new SetSMSAttributesRequest
        {
            Attributes = new() { ["DefaultSenderID"] = string.IsNullOrEmpty(originalValue) ? "" : originalValue },
        }));

        // DefaultSenderID must be alphanumeric and at most 11 characters
        var newValue = ("S2R" + Guid.NewGuid().ToString("N").ToUpperInvariant())[..11];
        await sns.SetSMSAttributesAsync(new SetSMSAttributesRequest { Attributes = new() { ["DefaultSenderID"] = newValue } });

        var after = (await sns.GetSMSAttributesAsync(new GetSMSAttributesRequest())).Attributes ?? [];
        Ensure(after.GetValueOrDefault("DefaultSenderID") == newValue,
            $"expected DefaultSenderID {Py.Repr(newValue)} to round-trip through " +
            $"GetSMSAttributes, got {Py.Repr(after.GetValueOrDefault("DefaultSenderID"))}");

        var unrelatedBefore = before.Where(kv => kv.Key != "DefaultSenderID").ToDictionary(kv => kv.Key, kv => kv.Value);
        var unrelatedAfter = after.Where(kv => kv.Key != "DefaultSenderID").ToDictionary(kv => kv.Key, kv => kv.Value);
        Ensure(Py.Equal(unrelatedAfter, unrelatedBefore),
            "SetSMSAttributes with only DefaultSenderID must not disturb other " +
            $"account attributes: before={Py.Repr(unrelatedBefore)} after={Py.Repr(unrelatedAfter)}");
    }
}

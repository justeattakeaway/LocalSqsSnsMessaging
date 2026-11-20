// Translated from behavioral/events__s3__sqs/check_xsvc_s3_1_key_prefix_filter_withholds_near_miss_keys.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;

namespace InMemoryAws.Tests.AwsEvaluationSuite.EventsS3Sqs;

/// <summary>events__s3__sqs.xsvc-s3-1-key-prefix-filter-withholds-near-miss-keys</summary>
[Category("AwsEvaluationSuite")]
public sealed class XsvcS31KeyPrefixFilterWithholdsNearMissKeys
{
    private const string Check = "xsvc-s3-1-key-prefix-filter-withholds-near-miss-keys";

    /// <summary>
    /// A prefix filter on detail.object.key delivers only keys that start with the prefix,
    /// case-sensitively, and withholds near-miss keys from the same bucket that an unfiltered rule
    /// receives.
    /// </summary>
    [Test]
    [DisplayName("events__s3__sqs.xsvc-s3-1-key-prefix-filter-withholds-near-miss-keys")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        await XsvcS3FilterScenario.Run(
            ctx,
            Check,
            // The content filter under test, applied to `detail.object` of an S3 "Object Created" event.
            new JsonObject { ["key"] = new JsonArray(new JsonObject { ["prefix"] = "incoming/" }) },
            // A freshly created rule/target can silently drop its very first delivered event, so the
            // warm-up retries with a FRESH key per attempt rather than a fixed one.
            attempt => ($"incoming/warmup-{attempt}.txt", 16),
            // Each one differs from a match in exactly one way.
            [("archive/incoming/a.txt", 16), ("Incoming/a.txt", 16), ("incoming-a.txt", 16)],
            // Put AFTER the near-misses on the same bucket and the same rule: the positive control.
            [("incoming/a.txt", 16)],
            new XsvcS3FilterScenario.Grading(MaxCredibleDeliveryWaitS: 90.0, TolerateUnconfirmedWarmups: false, WaitForZeroCounters: false));
    }
}

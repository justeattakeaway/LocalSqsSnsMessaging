// Translated from behavioral/events__s3__sqs/check_xsvc_s3_1_key_anything_but_filter_withholds_excluded_keys.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;

namespace InMemoryAws.Tests.AwsEvaluationSuite.EventsS3Sqs;

/// <summary>events__s3__sqs.xsvc-s3-1-key-anything-but-filter-withholds-excluded-keys</summary>
[Category("AwsEvaluationSuite")]
public sealed class XsvcS31KeyAnythingButFilterWithholdsExcludedKeys
{
    private const string Check = "xsvc-s3-1-key-anything-but-filter-withholds-excluded-keys";

    /// <summary>
    /// An anything-but filter on detail.object.key withholds exactly the listed keys and delivers
    /// every other key from the same bucket, including keys one edit away from an excluded value.
    /// </summary>
    [Test]
    [DisplayName("events__s3__sqs.xsvc-s3-1-key-anything-but-filter-withholds-excluded-keys")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        await XsvcS3FilterScenario.Run(
            ctx,
            Check,
            // The content filter under test, applied to `detail.object` of an S3 "Object Created" event.
            new JsonObject { ["key"] = new JsonArray(new JsonObject { ["anything-but"] = new JsonArray("tmp/scratch.txt", "tmp/cache.bin") }) },
            // A freshly created rule/target can silently drop its very first delivered event, so the
            // warm-up retries with a FRESH key per attempt rather than a fixed one.
            attempt => ($"tmp/warmup-{attempt}.txt", 16),
            // Each one differs from a match in exactly one way.
            [("tmp/scratch.txt", 16), ("tmp/cache.bin", 16)],
            // Put AFTER the near-misses on the same bucket and the same rule: the positive control.
            [("tmp/scratch.txt2", 16), ("tmp/Scratch.txt", 16), ("tmp/cache", 16)],
            new XsvcS3FilterScenario.Grading(MaxCredibleDeliveryWaitS: 180.0, TolerateUnconfirmedWarmups: false, WaitForZeroCounters: false));
    }
}

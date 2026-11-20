// Translated from behavioral/events__s3__sqs/check_xsvc_s3_1_size_numeric_range_filter_withholds_out_of_range_objects.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;

namespace InMemoryAws.Tests.AwsEvaluationSuite.EventsS3Sqs;

/// <summary>events__s3__sqs.xsvc-s3-1-size-numeric-range-filter-withholds-out-of-range-objects</summary>
[Category("AwsEvaluationSuite")]
public sealed class XsvcS31SizeNumericRangeFilterWithholdsOutOfRangeObjects
{
    private const string Check = "xsvc-s3-1-size-numeric-range-filter-withholds-out-of-range-objects";

    /// <summary>
    /// An EventBridge <c>numeric</c> filter on <c>detail.object.size</c> delivers exactly the S3 objects of
    /// 100 and 199 bytes for the range [100, 200) and withholds the 99- and 200-byte objects that an
    /// unfiltered observer rule receives, each envelope carrying the size put.
    /// </summary>
    [Test]
    [DisplayName("events__s3__sqs.xsvc-s3-1-size-numeric-range-filter-withholds-out-of-range-objects")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        await XsvcS3FilterScenario.Run(
            ctx,
            Check,
            // The content filter under test, applied to `detail.object` of an S3 "Object Created" event.
            new JsonObject { ["size"] = new JsonArray(new JsonObject { ["numeric"] = new JsonArray(">=", 100, "<", 200) }) },
            // A freshly created rule/target can silently drop its very first delivered event, so the
            // warm-up retries with a FRESH key per attempt rather than a fixed one.
            attempt => ($"warmup-{attempt}.bin", 150),
            // Each one differs from a match in exactly one way.
            [("below-lower.bin", 99), ("at-upper.bin", 200)],
            // Put AFTER the near-misses on the same bucket and the same rule: the positive control.
            [("at-lower.bin", 100), ("below-upper.bin", 199)],
            new XsvcS3FilterScenario.Grading(MaxCredibleDeliveryWaitS: 180.0, TolerateUnconfirmedWarmups: true, WaitForZeroCounters: true));
    }
}

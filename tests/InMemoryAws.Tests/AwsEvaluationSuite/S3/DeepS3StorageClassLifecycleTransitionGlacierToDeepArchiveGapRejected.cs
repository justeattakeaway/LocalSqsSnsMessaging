// Translated from behavioral/s3/check_deep_s3_storage_class_lifecycle_transition_glacier_to_deep_archive_gap_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.deep-s3-storage-class-lifecycle-transition-glacier-to-deep-archive-gap-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeepS3StorageClassLifecycleTransitionGlacierToDeepArchiveGapRejected
{
    /// <summary>
    /// GLACIER@30 -> DEEP_ARCHIVE@60 is refused with InvalidArgument (GLACIER's 90-day minimum),
    /// GLACIER@30 -> DEEP_ARCHIVE@120 is accepted, and the stored rule survives the refused PUT.
    /// </summary>
    [Test]
    [DisplayName("s3.deep-s3-storage-class-lifecycle-transition-glacier-to-deep-archive-gap-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-dsc-ld-");

        await s3.PutLifecycleConfigurationAsync(new PutLifecycleConfigurationRequest { BucketName = bucket, Configuration = new LifecycleConfiguration { Rules = [Baseline()] } });
        var baseline = S3Fidelity.CanonTransitions([S3Fidelity.Boto(Baseline())]);
        var stored = await S3Fidelity.PollCanonRules(s3, bucket, baseline);
        Ensure(Py.Equal(stored, baseline), $"90-day-gap baseline read back as {Py.Repr(stored)}");

        // Same classes and order as the baseline; only the gap differs (STANDARD_IA's minimum is 30).
        var bad = new LifecycleRule
        {
            Id = "glacier-30-deep-60",
            Status = LifecycleRuleStatus.Enabled,
            Filter = new LifecycleFilter { Prefix = "archive/" },
            Transitions = [new LifecycleTransition { Days = 30, StorageClass = new S3StorageClass("GLACIER") }, new LifecycleTransition { Days = 60, StorageClass = new S3StorageClass("DEEP_ARCHIVE") }],
        };
        var err = await FidelityContext.CaptureError(() => s3.PutLifecycleConfigurationAsync(new PutLifecycleConfigurationRequest
        {
            BucketName = bucket,
            Configuration = new LifecycleConfiguration { Rules = [bad] },
        })) ?? throw Fail("expected a ClientError, but the call SUCCEEDED");
        Ensure(err.Code == "InvalidArgument", $"GLACIER@30 -> DEEP_ARCHIVE@60 returned {err}, expected InvalidArgument");
        Ensure(err.Message == "'Days' in the 'Transition' action for StorageClass 'DEEP_ARCHIVE' for filter '(prefix=archive/)' must be 90 days more than 'filter '(prefix=archive/)'' in the 'Transition' action for StorageClass 'GLACIER'", err.ToString());

        var after = await S3Fidelity.CanonRules(s3, bucket);
        Ensure(Py.Equal(after, baseline), $"a refused PUT changed the stored rules to {Py.Repr(after)}");
    }

    private static LifecycleRule Baseline() => new()
    {
        Id = "glacier-30-deep-120",
        Status = LifecycleRuleStatus.Enabled,
        Filter = new LifecycleFilter { Prefix = "archive/" },
        Transitions = [new LifecycleTransition { Days = 30, StorageClass = new S3StorageClass("GLACIER") }, new LifecycleTransition { Days = 120, StorageClass = new S3StorageClass("DEEP_ARCHIVE") }],
    };
}

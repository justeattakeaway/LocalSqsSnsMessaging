// Translated from behavioral/s3/check_deep_s3_storage_class_lifecycle_transition_ia_to_glacier_gap_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.deep-s3-storage-class-lifecycle-transition-ia-to-glacier-gap-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeepS3StorageClassLifecycleTransitionIaToGlacierGapRejected
{
    /// <summary>
    /// PutBucketLifecycleConfiguration accepts STANDARD_IA@30 -> GLACIER@60 and refuses STANDARD_IA@30
    /// -> GLACIER@40 with InvalidArgument and its exact message (STANDARD_IA's 30-day minimum), leaving the
    /// stored rules unchanged.
    /// </summary>
    [Test]
    [DisplayName("s3.deep-s3-storage-class-lifecycle-transition-ia-to-glacier-gap-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-dsc-lg-");

        await s3.PutLifecycleConfigurationAsync(new PutLifecycleConfigurationRequest { BucketName = bucket, Configuration = new LifecycleConfiguration { Rules = [Baseline()] } });
        var baseline = S3Fidelity.CanonTransitions([S3Fidelity.Boto(Baseline())]);
        var stored = await S3Fidelity.PollCanonRules(s3, bucket, baseline);
        Ensure(Py.Equal(stored, baseline), $"30-day-gap baseline read back as {Py.Repr(stored)}");

        var bad = new LifecycleRule
        {
            Id = "ia-30-glacier-40",
            Status = LifecycleRuleStatus.Enabled,
            Filter = new LifecycleFilter { Prefix = "tiered/" },
            Transitions = [new LifecycleTransition { Days = 30, StorageClass = new S3StorageClass("STANDARD_IA") }, new LifecycleTransition { Days = 40, StorageClass = new S3StorageClass("GLACIER") }],
        };
        var err = await FidelityContext.CaptureError(() => s3.PutLifecycleConfigurationAsync(new PutLifecycleConfigurationRequest
        {
            BucketName = bucket,
            Configuration = new LifecycleConfiguration { Rules = [bad] },
        })) ?? throw Fail("expected a ClientError, but the call SUCCEEDED");
        Ensure(err.Code == "InvalidArgument", $"STANDARD_IA@30 -> GLACIER@40 returned {err}, expected InvalidArgument");
        Ensure(err.Message == "'Days' in the 'Transition' action for StorageClass 'GLACIER' for filter '(prefix=tiered/)' must be 30 days more than 'filter '(prefix=tiered/)'' in the 'Transition' action for StorageClass 'STANDARD_IA'", err.ToString());

        var after = await S3Fidelity.CanonRules(s3, bucket);
        Ensure(Py.Equal(after, baseline), $"a refused PUT changed the stored rules to {Py.Repr(after)}");
    }

    private static LifecycleRule Baseline() => new()
    {
        Id = "ia-30-glacier-60",
        Status = LifecycleRuleStatus.Enabled,
        Filter = new LifecycleFilter { Prefix = "tiered/" },
        Transitions = [new LifecycleTransition { Days = 30, StorageClass = new S3StorageClass("STANDARD_IA") }, new LifecycleTransition { Days = 60, StorageClass = new S3StorageClass("GLACIER") }],
    };
}

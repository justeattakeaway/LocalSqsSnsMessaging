// Translated from behavioral/s3/check_deep_s3_storage_class_lifecycle_transition_colder_before_warmer_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.deep-s3-storage-class-lifecycle-transition-colder-before-warmer-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeepS3StorageClassLifecycleTransitionColderBeforeWarmerRejected
{
    /// <summary>
    /// PutBucketLifecycleConfiguration accepts STANDARD_IA@30 -> GLACIER@60 and refuses GLACIER@30 ->
    /// STANDARD_IA@60 with InvalidArgument and its exact message, leaving the stored rules unchanged:
    /// transitions within a rule must run warm to cold.
    /// </summary>
    [Test]
    [DisplayName("s3.deep-s3-storage-class-lifecycle-transition-colder-before-warmer-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-dsc-lo-");

        await s3.PutLifecycleConfigurationAsync(new PutLifecycleConfigurationRequest { BucketName = bucket, Configuration = new LifecycleConfiguration { Rules = [Baseline()] } });
        var baseline = S3Fidelity.CanonTransitions([S3Fidelity.Boto(Baseline())]);
        var stored = await S3Fidelity.PollCanonRules(s3, bucket, baseline);
        Ensure(Py.Equal(stored, baseline), $"warm->cold baseline read back as {Py.Repr(stored)}");

        var bad = new LifecycleRule
        {
            Id = "glacier-then-ia",
            Status = LifecycleRuleStatus.Enabled,
            Filter = new LifecycleFilter { Prefix = "tiered/" },
            Transitions = [new LifecycleTransition { Days = 30, StorageClass = new S3StorageClass("GLACIER") }, new LifecycleTransition { Days = 60, StorageClass = new S3StorageClass("STANDARD_IA") }],
        };
        var err = await FidelityContext.CaptureError(() => s3.PutLifecycleConfigurationAsync(new PutLifecycleConfigurationRequest
        {
            BucketName = bucket,
            Configuration = new LifecycleConfiguration { Rules = [bad] },
        })) ?? throw Fail("expected a ClientError, but the call SUCCEEDED");
        Ensure(err.Code == "InvalidArgument", $"GLACIER@30 -> STANDARD_IA@60 returned {err}, expected InvalidArgument");
        Ensure(err.Message == "'Days' in the 'Transition' action for StorageClass 'GLACIER' for filter '(prefix=tiered/)' must be greater than 'Days' in the 'Transition' action for StorageClass 'STANDARD_IA' for filter '(prefix=tiered/)'", err.ToString());

        var after = await S3Fidelity.CanonRules(s3, bucket);
        Ensure(Py.Equal(after, baseline), $"a refused PUT changed the stored rules to {Py.Repr(after)}");
    }

    private static LifecycleRule Baseline() => new()
    {
        Id = "ia-then-glacier",
        Status = LifecycleRuleStatus.Enabled,
        Filter = new LifecycleFilter { Prefix = "tiered/" },
        Transitions = [new LifecycleTransition { Days = 30, StorageClass = new S3StorageClass("STANDARD_IA") }, new LifecycleTransition { Days = 60, StorageClass = new S3StorageClass("GLACIER") }],
    };
}

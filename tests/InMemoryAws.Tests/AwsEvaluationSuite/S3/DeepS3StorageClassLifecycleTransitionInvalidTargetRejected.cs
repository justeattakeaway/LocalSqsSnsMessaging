// Translated from behavioral/s3/check_deep_s3_storage_class_lifecycle_transition_invalid_target_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.deep-s3-storage-class-lifecycle-transition-invalid-target-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeepS3StorageClassLifecycleTransitionInvalidTargetRejected
{
    /// <summary>
    /// PutBucketLifecycleConfiguration with a Transition to STANDARD is refused with InvalidArgument and
    /// its exact message, and the rules stored before it read back unchanged: a refused PUT is atomic.
    /// </summary>
    [Test]
    [DisplayName("s3.deep-s3-storage-class-lifecycle-transition-invalid-target-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-dsc-lt-");

        // Positive control: a valid transition target is accepted and stored.
        await s3.PutLifecycleConfigurationAsync(new PutLifecycleConfigurationRequest { BucketName = bucket, Configuration = new LifecycleConfiguration { Rules = [Baseline()] } });
        var baseline = S3Fidelity.CanonTransitions([S3Fidelity.Boto(Baseline())]);
        var stored = await S3Fidelity.PollCanonRules(s3, bucket, baseline);
        Ensure(Py.Equal(stored, baseline), $"baseline read back as {Py.Repr(stored)}");

        var bad = new LifecycleRule
        {
            Id = "to-standard",
            Status = LifecycleRuleStatus.Enabled,
            Filter = new LifecycleFilter { Prefix = "cold/" },
            Transitions = [new LifecycleTransition { Days = 30, StorageClass = new S3StorageClass("STANDARD") }],
        };
        var err = await FidelityContext.CaptureError(() => s3.PutLifecycleConfigurationAsync(new PutLifecycleConfigurationRequest
        {
            BucketName = bucket,
            Configuration = new LifecycleConfiguration { Rules = [bad] },
        })) ?? throw Fail("expected a ClientError, but the call SUCCEEDED");
        Ensure(err.Code == "InvalidArgument", $"Transition to STANDARD returned {err}, expected InvalidArgument");
        Ensure(err.Message == "Invalid target Storage class for Transition action", err.ToString());

        var after = await S3Fidelity.CanonRules(s3, bucket);
        Ensure(Py.Equal(after, baseline), $"a refused PUT changed the stored rules to {Py.Repr(after)}");
    }

    private static LifecycleRule Baseline() => new()
    {
        Id = "baseline-to-glacier",
        Status = LifecycleRuleStatus.Enabled,
        Filter = new LifecycleFilter { Prefix = "cold/" },
        Transitions = [new LifecycleTransition { Days = 30, StorageClass = new S3StorageClass("GLACIER") }],
    };
}

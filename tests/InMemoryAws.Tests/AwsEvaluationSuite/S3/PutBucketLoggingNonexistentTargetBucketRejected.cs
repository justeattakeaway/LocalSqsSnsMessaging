// Translated from behavioral/s3/check_put_bucket_logging_nonexistent_target_bucket_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.put-bucket-logging-nonexistent-target-bucket-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutBucketLoggingNonexistentTargetBucketRejected
{
    /// <summary>
    /// PutBucketLogging naming a TargetBucket that does not exist is rejected rather than
    /// accepted and silently un-deliverable; the identical call naming a bucket this account
    /// owns is accepted.
    /// </summary>
    [Test]
    [DisplayName("s3.put-bucket-logging-nonexistent-target-bucket-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;

        var source = await S3Fidelity.CreateBucket(ctx, "sim2real-logsrc-");

        var ghostTarget = FidelityContext.UniqueName("sim2real-logtgt-ghost-");
        var (code, message) = await S3Fidelity.ErrorPair(
            () => s3.PutBucketLoggingAsync(new PutBucketLoggingRequest
            {
                BucketName = source,
                LoggingConfig = new S3BucketLoggingConfig { TargetBucketName = ghostTarget, TargetPrefix = "logs/" },
            }),
            "expected the call to be rejected, but it succeeded");
        Ensure(code == "InvalidTargetBucketForLogging", $"expected InvalidTargetBucketForLogging, got {Py.Repr(code)}: {Py.Repr(message)}");
        Ensure(message == "The target bucket for logging does not exist", $"unexpected message {Py.Repr(message)}");

        // effect absent: the rejected PUT never set a logging configuration on the source
        var unset = await s3.GetBucketLoggingAsync(new GetBucketLoggingRequest { BucketName = source });
        Ensure(
            string.IsNullOrEmpty(unset.BucketLoggingConfig?.TargetBucketName),
            $"expected no LoggingEnabled after the rejected PUT, got {Py.Repr(unset.BucketLoggingConfig?.TargetBucketName)}");

        // positive control: the identical call naming a bucket this account actually owns is accepted
        var realTarget = await S3Fidelity.CreateBucket(ctx, "sim2real-logtgt-");
        await s3.PutBucketLoggingAsync(new PutBucketLoggingRequest
        {
            BucketName = source,
            LoggingConfig = new S3BucketLoggingConfig { TargetBucketName = realTarget, TargetPrefix = "logs/" },
        });
        var enabled = (await s3.GetBucketLoggingAsync(new GetBucketLoggingRequest { BucketName = source })).BucketLoggingConfig;
        Ensure(enabled?.TargetBucketName == realTarget, $"expected TargetBucket {Py.Repr(realTarget)}, got {Py.Repr(enabled?.TargetBucketName)}");
    }
}

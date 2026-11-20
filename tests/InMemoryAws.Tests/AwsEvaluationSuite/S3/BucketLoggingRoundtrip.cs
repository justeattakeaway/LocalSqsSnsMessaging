// Translated from behavioral/s3/check_bucket_logging_roundtrip.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3.Model;
using Amazon.S3;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.bucket-logging-roundtrip</summary>
[Category("AwsEvaluationSuite")]
public sealed class BucketLoggingRoundtrip
{
    /// <summary>
    /// A fresh bucket starts with logging disabled (GetBucketLogging returns no LoggingEnabled block);
    /// PutBucketLogging enables a target bucket/prefix and GetBucketLogging reflects the new state.
    /// </summary>
    [Test]
    [DisplayName("s3.bucket-logging-roundtrip")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var src = await S3Fidelity.NewBucket(ctx, "sim2real-behav-log-src-");
        var target = await S3Fidelity.NewBucket(ctx, "sim2real-behav-log-tgt-");
        var initial = (await s3.GetBucketLoggingAsync(new GetBucketLoggingRequest { BucketName = src })).BucketLoggingConfig;
        Ensure(initial?.TargetBucketName is null, $"LoggingEnabled present on a fresh bucket: {Py.Repr(initial?.TargetBucketName)}");

        // Allow ACL usage on the target bucket, and grant the S3 log delivery group the
        // permissions it needs to write access logs there.
        await s3.PutBucketOwnershipControlsAsync(new PutBucketOwnershipControlsRequest
        {
            BucketName = target,
            OwnershipControls = new OwnershipControls { Rules = [new OwnershipControlsRule { ObjectOwnership = ObjectOwnership.BucketOwnerPreferred }] },
        });
        var owner = (await s3.GetBucketAclAsync(new GetBucketAclRequest { BucketName = target })).Owner;
        await s3.PutBucketAclAsync(new PutBucketAclRequest
        {
            BucketName = target,
            AccessControlPolicy = new S3AccessControlList
            {
                Owner = owner,
                Grants =
                [
                    S3Fidelity.UserGrant(owner.Id, S3Permission.FULL_CONTROL),
                    S3Fidelity.GroupGrant(S3Fidelity.LogDelivery, S3Permission.WRITE),
                    S3Fidelity.GroupGrant(S3Fidelity.LogDelivery, S3Permission.READ_ACP),
                ],
            },
        });
        const string prefix = "access-logs/";
        await s3.PutBucketLoggingAsync(new PutBucketLoggingRequest
        {
            BucketName = src,
            LoggingConfig = new S3BucketLoggingConfig { TargetBucketName = target, TargetPrefix = prefix },
        });
        var enabled = (await s3.GetBucketLoggingAsync(new GetBucketLoggingRequest { BucketName = src })).BucketLoggingConfig;
        Ensure(enabled?.TargetBucketName == target, $"{Py.Repr(enabled?.TargetBucketName)}");
        Ensure(enabled?.TargetPrefix == prefix, $"{Py.Repr(enabled?.TargetPrefix)}");
    }
}

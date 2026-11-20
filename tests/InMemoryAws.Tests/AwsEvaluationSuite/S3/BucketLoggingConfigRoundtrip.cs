// Translated from behavioral/s3/check_bucket_logging_config_roundtrip.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3.Model;
using Amazon.S3;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.bucket-logging-config-roundtrip</summary>
[Category("AwsEvaluationSuite")]
public sealed class BucketLoggingConfigRoundtrip
{
    /// <summary>
    /// Server access logging target/prefix set via PutBucketLogging is reflected exactly by
    /// GetBucketLogging, and disabling logging (empty status) reverts GetBucketLogging to empty.
    /// </summary>
    [Test]
    [DisplayName("s3.bucket-logging-config-roundtrip")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var target = await S3Fidelity.NewBucket(ctx, "sim2real-logtarget-", ObjectOwnership.BucketOwnerPreferred);
        await GrantLogDelivery(s3, target);
        var source = await S3Fidelity.NewBucket(ctx, "sim2real-logsrc-");
        await s3.PutBucketLoggingAsync(new PutBucketLoggingRequest
        {
            BucketName = source,
            LoggingConfig = new S3BucketLoggingConfig { TargetBucketName = target, TargetPrefix = "logs/" },
        });
        var enabled = (await s3.GetBucketLoggingAsync(new GetBucketLoggingRequest { BucketName = source })).BucketLoggingConfig;
        Ensure(enabled?.TargetBucketName == target, $"{Py.Repr(enabled?.TargetBucketName)}");
        Ensure(enabled?.TargetPrefix == "logs/", $"{Py.Repr(enabled?.TargetPrefix)}");

        // Disabling logging (empty status document) reverts to no logging configuration.
        await s3.PutBucketLoggingAsync(new PutBucketLoggingRequest { BucketName = source, LoggingConfig = new S3BucketLoggingConfig() });
        var after = (await s3.GetBucketLoggingAsync(new GetBucketLoggingRequest { BucketName = source })).BucketLoggingConfig;
        Ensure(after?.TargetBucketName is null, $"LoggingEnabled still present: {Py.Repr(after?.TargetBucketName)}");
    }

    /// <summary>
    /// Grant the S3 LogDelivery group WRITE + READ_ACP so PutBucketLogging accepts this bucket as a
    /// valid logging target (this is not a public grant, so account-level Block Public Access settings
    /// do not interfere with it).
    /// </summary>
    private static async Task GrantLogDelivery(IAmazonS3 s3, string bucket)
    {
        var owner = (await s3.GetBucketAclAsync(new GetBucketAclRequest { BucketName = bucket })).Owner;
        List<S3Grant> grants = [S3Fidelity.UserGrant(owner.Id, S3Permission.FULL_CONTROL)];
        foreach (var permission in new[] { S3Permission.WRITE, S3Permission.READ_ACP })
        {
            grants.Add(S3Fidelity.GroupGrant(S3Fidelity.LogDelivery, permission));
        }

        await s3.PutBucketAclAsync(new PutBucketAclRequest { BucketName = bucket, AccessControlPolicy = new S3AccessControlList { Owner = owner, Grants = grants } });
    }
}

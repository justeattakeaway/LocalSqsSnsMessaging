// Translated from behavioral/s3/check_get_bucket_replication_not_configured_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.get-bucket-replication-not-configured-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class GetBucketReplicationNotConfiguredRejected
{
    /// <summary>
    /// GetBucketReplication on a versioned bucket with no replication config fails with
    /// ReplicationConfigurationNotFoundError; once one is set, the same call returns it.
    /// </summary>
    [Test]
    [DisplayName("s3.get-bucket-replication-not-configured-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var src = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        await EnableVersioning(s3, src);

        // The .NET SDK answers GetBucketReplication's 404 as an empty success; the refusal is read off the wire.

        var neverConfigured = await S3Fidelity.RawError(ctx, "GET", $"{src}?replication");
        Ensure(
            neverConfigured.Code == "ReplicationConfigurationNotFoundError",
            $"expected ReplicationConfigurationNotFoundError, got {neverConfigured}");
        Ensure(neverConfigured.Message == "The replication configuration was not found", neverConfigured.Message ?? "None");

        var dst = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        await EnableVersioning(s3, dst);
        var dstArn = $"arn:aws:s3:::{dst}";

        // The check creates an IAM role S3 can assume only to name it here; the cloud models no IAM.
        var roleArn = ctx.RoleArn(FidelityContext.UniqueName("sim2real-behav-repl-role-"));
        await S3Fidelity.PollUntil(
            () => S3Fidelity.Try(() => S3Fidelity.PutReplication(s3, src, roleArn, S3Fidelity.Rule("sim2real-rule", 1, "", dstArn))),
            code => code == S3Fidelity.Succeeded,
            TimeSpan.FromSeconds(60.0),
            TimeSpan.FromSeconds(3.0),
            last => $"PutBucketReplication never succeeded within 60.0s: {last}");

        var nowConfigured = (await s3.GetBucketReplicationAsync(new GetBucketReplicationRequest { BucketName = src })).Configuration;
        Ensure(nowConfigured.Role == roleArn, Py.Repr(nowConfigured.Role));
        Ensure(nowConfigured.Rules[0].Status == ReplicationRuleStatus.Enabled, Py.Repr(nowConfigured.Rules[0].Status?.Value));
        Ensure(nowConfigured.Rules[0].Destination.BucketArn == dstArn, Py.Repr(nowConfigured.Rules[0].Destination.BucketArn));
    }

    private static Task EnableVersioning(Amazon.S3.IAmazonS3 s3, string bucket) =>
        s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = bucket,
            VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled },
        });
}

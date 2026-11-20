// Translated from behavioral/s3/check_bucket_replication_configuration_lifecycle.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.bucket-replication-configuration-lifecycle</summary>
[Category("AwsEvaluationSuite")]
public sealed class BucketReplicationConfigurationLifecycle
{
    /// <summary>
    /// PutBucketReplication on a versioned bucket set -> Get reflects the rules;
    /// DeleteBucketReplication removes the configuration entirely.
    /// </summary>
    [Test]
    [DisplayName("s3.bucket-replication-configuration-lifecycle")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var src = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-repl-src-");
        var dst = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-repl-dst-");
        await EnableVersioning(s3, src);
        await EnableVersioning(s3, dst);

        // The check creates an IAM role S3 can assume only to name it here; the cloud models no IAM.
        var roleArn = ctx.RoleArn(FidelityContext.UniqueName("sim2real-behav-repl-role-"));
        var dstArn = $"arn:aws:s3:::{dst}";

        // IAM role propagation to S3's replication engine can lag a few seconds behind role
        // creation; retry the PutBucketReplication call in a bounded loop instead of a fixed sleep.
        await S3Fidelity.PollUntil(
            () => S3Fidelity.Try(() => S3Fidelity.PutReplication(s3, src, roleArn, S3Fidelity.Rule("rule1", 1, "", dstArn))),
            code => code == S3Fidelity.Succeeded,
            TimeSpan.FromSeconds(60.0),
            TimeSpan.FromSeconds(3.0),
            last => $"PutBucketReplication never accepted the replication role within 60.0s: {last}");

        var got = (await s3.GetBucketReplicationAsync(new GetBucketReplicationRequest { BucketName = src })).Configuration;
        var rules = got.Rules;
        Ensure(rules.Count == 1, $"{rules.Count} rules");
        Ensure(rules[0].Destination.BucketArn == dstArn, Py.Repr(rules[0].Destination.BucketArn));
        Ensure(got.Role == roleArn, Py.Repr(got.Role));

        await s3.DeleteBucketReplicationAsync(new DeleteBucketReplicationRequest { BucketName = src });
        // The .NET SDK answers GetBucketReplication's 404 as an empty success; the refusal is read off the wire.
        var error = await S3Fidelity.RawError(ctx, "GET", $"{src}?replication");
        Ensure(error.Code == "ReplicationConfigurationNotFoundError", error.Code ?? "None");
        Ensure(error.Message == "The replication configuration was not found", error.Message ?? "None");
    }

    private static Task EnableVersioning(Amazon.S3.IAmazonS3 s3, string bucket) =>
        s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = bucket,
            VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled },
        });
}

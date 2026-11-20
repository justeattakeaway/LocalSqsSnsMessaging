// Translated from behavioral/s3/check_replication_server_side_execution.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.replication-server-side-execution</summary>
[Category("AwsEvaluationSuite")]
public sealed class ReplicationServerSideExecution
{
    /// <summary>
    /// A configured replication rule causes PutObject writes to the source bucket to be replicated
    /// SERVER-SIDE into the destination bucket, with no client action beyond the original PutObject.
    /// </summary>
    [Test]
    [DisplayName("s3.replication-server-side-execution")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var sourceBucket = await NewVersionedBucket(ctx);
        var destBucket = await NewVersionedBucket(ctx);

        // The check creates an IAM role S3 can assume only to name it here; the cloud models no IAM.
        var roleArn = ctx.RoleArn(FidelityContext.UniqueName("sim2real-behav-repl-role-"));
        await S3Fidelity.PollUntil(
            () => S3Fidelity.Try(() => S3Fidelity.PutReplication(s3, sourceBucket, roleArn, S3Fidelity.Rule("sim2real-repl-rule", 1, "", $"arn:aws:s3:::{destBucket}"))),
            code => code == S3Fidelity.Succeeded,
            TimeSpan.FromSeconds(60),
            TimeSpan.FromSeconds(3),
            last => $"PutBucketReplication to accept the freshly-created replication role not observed within 60s; last saw {Py.Repr(last)}");

        var rules = (await s3.GetBucketReplicationAsync(new GetBucketReplicationRequest { BucketName = sourceBucket })).Configuration.Rules;
        Ensure(
            rules.Any(r => r.Destination.BucketArn == $"arn:aws:s3:::{destBucket}"),
            $"expected the destination bucket reflected in GetBucketReplication, got {rules.Count} rule(s)");

        const string key = "replicate-me.txt";
        const string data = "server-side-replicated-payload";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = sourceBucket, Key = key, ContentBody = data });

        await S3Fidelity.PollUntil(
            () => FidelityContext.CaptureError(() => s3.GetObjectMetadataAsync(destBucket, key)),
            error => error is null,
            TimeSpan.FromSeconds(300),
            TimeSpan.FromSeconds(10),
            last => $"PutObject to be server-side replicated into the destination bucket not observed within 300s; last saw {last?.Code}");
        var replicated = System.Text.Encoding.UTF8.GetString(await S3Fidelity.GetBytes(s3, destBucket, key));
        Ensure(replicated == data, "the replicated object body must match the source exactly, with no client re-upload");
    }

    /// <summary>Create a bucket with versioning Enabled (teardown registered).</summary>
    private static async Task<string> NewVersionedBucket(FidelityContext ctx)
    {
        var name = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        await ctx.S3.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = name,
            VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled },
        });
        return name;
    }
}

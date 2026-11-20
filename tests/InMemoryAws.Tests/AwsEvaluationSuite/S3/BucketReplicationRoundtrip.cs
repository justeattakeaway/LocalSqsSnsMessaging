// Translated from behavioral/s3/check_bucket_replication_roundtrip.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.bucket-replication-roundtrip</summary>
[Category("AwsEvaluationSuite")]
public sealed class BucketReplicationRoundtrip
{
    /// <summary>
    /// PutBucketReplication configuration is retrievable and removable via DeleteBucketReplication.
    /// Requires versioning enabled on both source and destination, and a real IAM role S3 can assume
    /// (iam:PassRole validates the role actually exists).
    /// </summary>
    [Test]
    [DisplayName("s3.bucket-replication-roundtrip")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var src = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        var dst = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        foreach (var bucket in new[] { src, dst })
        {
            await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
            {
                BucketName = bucket,
                VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled },
            });
        }

        // The check creates an IAM role S3 can assume only to name it here; the cloud models no IAM.
        var roleArn = ctx.RoleArn(FidelityContext.UniqueName("sim2real-behav-repl-role-"));
        var rule = S3Fidelity.Rule("sim2real-rule", 1, "", $"arn:aws:s3:::{dst}");
        await S3Fidelity.PollUntil(
            () => S3Fidelity.Try(() => S3Fidelity.PutReplication(s3, src, roleArn, rule)),
            code => code == S3Fidelity.Succeeded,
            TimeSpan.FromSeconds(60),
            TimeSpan.FromSeconds(3),
            last => $"PutBucketReplication to accept the freshly-created IAM role did not succeed within 60s: {last}");

        var fetched = (await s3.GetBucketReplicationAsync(new GetBucketReplicationRequest { BucketName = src })).Configuration;
        Ensure(fetched.Role == roleArn, $"expected Role {Py.Repr(roleArn)}, got {Py.Repr(fetched.Role)}");
        Ensure(fetched.Rules.Count == 1, $"expected exactly 1 rule, got {fetched.Rules.Count} rules");
        var fetchedRule = fetched.Rules[0];
        Ensure(fetchedRule.Status == ReplicationRuleStatus.Enabled, $"expected rule Status 'Enabled', got {Py.Repr(fetchedRule.Status?.Value)}");
        Ensure(
            fetchedRule.Destination.BucketArn == $"arn:aws:s3:::{dst}",
            $"expected destination bucket arn:aws:s3:::{dst}, got {Py.Repr(fetchedRule.Destination.BucketArn)}");

        await s3.DeleteBucketReplicationAsync(new DeleteBucketReplicationRequest { BucketName = src });
        // The .NET SDK answers GetBucketReplication's 404 as an empty success; the refusal is read off the wire.
        var error = await S3Fidelity.RawError(ctx, "GET", $"{src}?replication");
        Ensure(error.Code == "ReplicationConfigurationNotFoundError", $"expected ReplicationConfigurationNotFoundError after delete, got {error.Code}");
        Ensure(error.Message == "The replication configuration was not found", error.Message ?? "None");
    }
}

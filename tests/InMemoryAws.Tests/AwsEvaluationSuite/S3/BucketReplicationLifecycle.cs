// Translated from behavioral/s3/check_bucket_replication_lifecycle.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.bucket-replication-lifecycle</summary>
[Category("AwsEvaluationSuite")]
public sealed class BucketReplicationLifecycle
{
    /// <summary>
    /// PutBucketReplication persists a replication rule set, GetBucketReplication returns it
    /// verbatim, and DeleteBucketReplication removes it so a subsequent Get reverts to the
    /// ReplicationConfigurationNotFoundError state.
    /// </summary>
    [Test]
    [DisplayName("s3.bucket-replication-lifecycle")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var src = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-repl-src-");
        var dst = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-repl-dst-");
        List<Tag> tags = [new Tag { Key = "sim2real:managed", Value = "true" }, new Tag { Key = "sim2real:check", Value = "bucket-replication-lifecycle" }];
        await s3.PutBucketTaggingAsync(new PutBucketTaggingRequest { BucketName = src, TagSet = tags });
        await s3.PutBucketTaggingAsync(new PutBucketTaggingRequest { BucketName = dst, TagSet = tags });
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
        var ruleId = FidelityContext.UniqueName("rule-");
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(() => S3Fidelity.PutReplication(s3, src, roleArn, S3Fidelity.Rule(ruleId, 1, "", $"arn:aws:s3:::{dst}")));
            if (error is null)
            {
                break;
            }

            if (error.Code is "AccessDenied" or "MalformedPolicy" or "InvalidRequest" && clock.Elapsed < TimeSpan.FromSeconds(60.0))
            {
                await FidelityTime.Delay(TimeSpan.FromSeconds(3.0));
                continue;
            }

            throw error.Exception;
        }

        var cfg = (await s3.GetBucketReplicationAsync(new GetBucketReplicationRequest { BucketName = src })).Configuration;
        Ensure(cfg.Role == roleArn, $"expected Role {Py.Repr(roleArn)}, got {Py.Repr(cfg.Role)}");
        Ensure(cfg.Rules.Count == 1, $"expected exactly 1 rule, got {cfg.Rules.Count}");
        Ensure(cfg.Rules[0].Id == ruleId, $"expected rule ID {Py.Repr(ruleId)}, got {Py.Repr(cfg.Rules[0].Id)}");
        Ensure(cfg.Rules[0].Status == ReplicationRuleStatus.Enabled, $"expected rule Status 'Enabled', got {Py.Repr(cfg.Rules[0].Status?.Value)}");
        Ensure(
            cfg.Rules[0].Destination.BucketArn == $"arn:aws:s3:::{dst}",
            $"expected destination arn:aws:s3:::{dst}, got {Py.Repr(cfg.Rules[0].Destination.BucketArn)}");

        await s3.DeleteBucketReplicationAsync(new DeleteBucketReplicationRequest { BucketName = src });
        // The .NET SDK answers GetBucketReplication's 404 as an empty success; the refusal is read off the wire.
        var gone = await S3Fidelity.RawError(ctx, "GET", $"{src}?replication");
        Ensure(gone.Code == "ReplicationConfigurationNotFoundError", $"expected ReplicationConfigurationNotFoundError after delete, got {Py.Repr(gone.Code)}");
        Ensure(gone.Message == "The replication configuration was not found", gone.Message ?? "None");
    }
}

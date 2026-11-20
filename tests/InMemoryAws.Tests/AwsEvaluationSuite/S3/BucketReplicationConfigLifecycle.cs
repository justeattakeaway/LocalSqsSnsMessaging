// Translated from behavioral/s3/check_bucket_replication_config_lifecycle.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.bucket-replication-config-lifecycle</summary>
[Category("AwsEvaluationSuite")]
public sealed class BucketReplicationConfigLifecycle
{
    /// <summary>
    /// Cross-bucket replication configuration round-trips through put/get exactly (role ARN and
    /// per-rule destination), and DeleteBucketReplication reverts the source bucket to having no
    /// replication configuration.
    /// </summary>
    [Test]
    [DisplayName("s3.bucket-replication-config-lifecycle")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var source = await S3Fidelity.CreateBucket(ctx, "sim2real-repl-src-");
        var dest = await S3Fidelity.CreateBucket(ctx, "sim2real-repl-dst-");
        foreach (var name in new[] { source, dest })
        {
            await s3.PutBucketTaggingAsync(new PutBucketTaggingRequest { BucketName = name, TagSet = [new Tag { Key = "sim2real", Value = "true" }] });
        }

        await EnableVersioning(s3, source);
        await EnableVersioning(s3, dest);

        // The check creates an IAM role S3 can assume only to name it here; the cloud models no IAM.
        var roleArn = ctx.RoleArn(FidelityContext.UniqueName("sim2real-repl-role-"));
        var destArn = $"arn:aws:s3:::{dest}";
        await S3Fidelity.PollUntil(
            () => S3Fidelity.Try(() => S3Fidelity.PutReplication(s3, source, roleArn, S3Fidelity.Rule("rule1", 1, "", destArn))),
            code => code == S3Fidelity.Succeeded,
            TimeSpan.FromSeconds(60.0),
            TimeSpan.FromSeconds(3.0),
            last => $"operation never succeeded within 60.0s: {last}");

        var got = (await s3.GetBucketReplicationAsync(new GetBucketReplicationRequest { BucketName = source })).Configuration;
        Ensure(got.Role == roleArn, $"expected Role {Py.Repr(roleArn)}, got {Py.Repr(got.Role)}");
        Ensure(got.Rules.Count == 1, $"expected exactly 1 rule, got {got.Rules.Count} rules");
        var rule = got.Rules[0];
        Ensure(rule.Id == "rule1", $"expected rule ID 'rule1' to round-trip, got {Py.Repr(rule.Id)}");
        Ensure(rule.Status == ReplicationRuleStatus.Enabled, $"expected rule Status 'Enabled', got {Py.Repr(rule.Status?.Value)}");
        Ensure(rule.Destination.BucketArn == destArn, $"expected Destination.Bucket {Py.Repr(destArn)}, got {Py.Repr(rule.Destination?.BucketArn)}");

        await s3.DeleteBucketReplicationAsync(new DeleteBucketReplicationRequest { BucketName = source });
        // The .NET SDK answers GetBucketReplication's 404 as an empty success; the refusal is read off the wire.
        var error = await S3Fidelity.RawError(ctx, "GET", $"{source}?replication");
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

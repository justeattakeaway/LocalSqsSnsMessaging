// Translated from behavioral/s3/check_bucket_replication_requires_versioning_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.bucket-replication-requires-versioning-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class BucketReplicationRequiresVersioningRejected
{
    private static readonly string[] RolePropagationCodes = ["AccessDenied", "MalformedPolicy"];

    /// <summary>
    /// PutBucketReplication on an unversioned source is rejected with InvalidRequest and its exact
    /// message and stores no configuration; after versioning is enabled the identical call succeeds and Get
    /// returns its Role and destination.
    /// </summary>
    [Test]
    [DisplayName("s3.bucket-replication-requires-versioning-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var src = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-replneg-src-");
        var dst = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-replneg-dst-");
        await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = dst,
            VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled },
        });

        // The check creates an IAM role S3 can assume only to name it here; the cloud models no IAM.
        var roleArn = ctx.RoleArn(FidelityContext.UniqueName("sim2real-behav-replneg-role-"));
        var rule = S3Fidelity.Rule("rule1", 1, "", $"arn:aws:s3:::{dst}");
        Task PutReplication() => S3Fidelity.PutReplication(s3, src, roleArn, rule);

        // Never retries past InvalidRequest -- that is the code under test, not a propagation symptom.
        AwsError error;
        var clock = FidelityTime.StartNew();
        while (true)
        {
            error = await S3Fidelity.Error(PutReplication);
            if (RolePropagationCodes.Contains(error.Code) && clock.Elapsed < TimeSpan.FromSeconds(60.0))
            {
                await FidelityTime.Delay(TimeSpan.FromSeconds(3.0));
                continue;
            }

            break;
        }

        Ensure(error.Code == "InvalidRequest", error.Code ?? "None");
        Ensure(error.Message == "Versioning must be 'Enabled' on the bucket to apply a replication configuration", error.Message ?? "None");

        // The .NET SDK answers GetBucketReplication's 404 as an empty success; the refusal is read off the wire.

        var error2 = await S3Fidelity.RawError(ctx, "GET", $"{src}?replication");
        Ensure(error2.Code == "ReplicationConfigurationNotFoundError", error2.Code ?? "None");
        Ensure(error2.Message == "The replication configuration was not found", error2.Message ?? "None");

        await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = src,
            VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled },
        });
        clock = FidelityTime.StartNew();
        while (await FidelityContext.CaptureError(PutReplication) is { } failure)
        {
            if (RolePropagationCodes.Contains(failure.Code) && clock.Elapsed < TimeSpan.FromSeconds(60.0))
            {
                await FidelityTime.Delay(TimeSpan.FromSeconds(3.0));
                continue;
            }

            throw failure.Exception;
        }

        var fetched = (await s3.GetBucketReplicationAsync(new GetBucketReplicationRequest { BucketName = src })).Configuration;
        Ensure(fetched.Role == roleArn, $"expected Role {roleArn}, got {fetched.Role}");
        Ensure(
            fetched.Rules[0].Destination.BucketArn == $"arn:aws:s3:::{dst}",
            $"expected destination arn:aws:s3:::{dst}, got {fetched.Rules[0].Destination.BucketArn}");
    }
}

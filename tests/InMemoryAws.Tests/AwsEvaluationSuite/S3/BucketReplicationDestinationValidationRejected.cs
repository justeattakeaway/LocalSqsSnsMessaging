// Translated from behavioral/s3/check_bucket_replication_destination_validation_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.bucket-replication-destination-validation-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class BucketReplicationDestinationValidationRejected
{
    private const string Check = "bucket-replication-destination-validation-rejected";
    private static readonly string[] RolePropagationCodes = ["AccessDenied", "MalformedPolicy"];

    /// <summary>
    /// PutBucketReplication rejects ReplicationTime without Metrics, duplicate rule Priority, and
    /// Filter without DeleteMarkerReplication, each with its exact message and no partial effect.
    /// A fully valid rule is accepted, and Metrics without ReplicationTime is accepted too.
    /// </summary>
    [Test]
    [DisplayName("s3.bucket-replication-destination-validation-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var src = await CreateTaggedBucket(ctx, s3, "sim2real-behav-replval-src-");
        var dst = await CreateTaggedBucket(ctx, s3, "sim2real-behav-replval-dst-");
        foreach (var bucket in new[] { src, dst })
        {
            await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
            {
                BucketName = bucket,
                VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled },
            });
        }

        var dstArn = $"arn:aws:s3:::{dst}";

        // The check creates a minimal IAM role S3 can assume only to name it here; the cloud models no IAM.
        var roleArn = ctx.RoleArn(FidelityContext.UniqueName("sim2real-behav-replval-role-"));
        Task Put(params ReplicationRule[] rules) => S3Fidelity.PutReplication(s3, src, roleArn, rules);

        var ruleRtOnly = S3Fidelity.Rule("rt-no-metrics", 1, "", dstArn);
        ruleRtOnly.Destination.ReplicationTime = new ReplicationTime
        {
            Status = ReplicationTimeStatus.Enabled,
            Time = new ReplicationTimeValue { Minutes = 15 },
        };
        var (code, message) = await RetryWhileRolePropagates(ctx, async () =>
        {
            var error = await S3Fidelity.Error(() => Put(ruleRtOnly), "expected the call to be rejected, but it succeeded");
            return (error.Code, error.Message);
        });
        Ensure(code == "InvalidRequest", $"expected InvalidRequest for ReplicationTime without Metrics, got {Py.Repr(code)}");
        Ensure(message == "Replication destination must contain both ReplicationTime and Metrics or neither.", message ?? "None");
        Ensure(await GetReplication(ctx, s3, src) is null, "a rejected PutBucketReplication must not apply any configuration");

        var ruleMetricsOnly = S3Fidelity.Rule("metrics-no-rt", 1, "", dstArn);
        ruleMetricsOnly.Destination.Metrics = new Metrics { Status = MetricsStatus.Enabled };
        await RetryUntilAccepted(ctx, () => Put(ruleMetricsOnly));
        var applied = await GetReplication(ctx, s3, src);
        Ensure(applied is not null, "Metrics without ReplicationTime must be accepted and read back");
        var destination = applied.Rules[0].Destination;
        Ensure(destination.Metrics?.Status == MetricsStatus.Enabled, $"expected Metrics.Status 'Enabled', got {Py.Repr(destination.Metrics?.Status?.Value)}");
        Ensure(destination.ReplicationTime is null, "expected no ReplicationTime in a Metrics-only destination");

        await s3.DeleteBucketReplicationAsync(new DeleteBucketReplicationRequest { BucketName = src });
        Ensure(await GetReplication(ctx, s3, src) is null, "DeleteBucketReplication must clear the configuration before the next rule is tried");

        const int dupPriority = 7;
        var ruleA = S3Fidelity.Rule("dup-a", dupPriority, "a/", dstArn);
        var ruleB = S3Fidelity.Rule("dup-b", dupPriority, "b/", dstArn);
        var error2 = await S3Fidelity.Error(() => Put(ruleA, ruleB), "expected the call to be rejected, but it succeeded");
        Ensure(error2.Code == "InvalidRequest", $"expected InvalidRequest for duplicate Priority, got {Py.Repr(error2.Code)}");
        Ensure(error2.Message == $"Found duplicate priority {dupPriority}.", error2.Message ?? "None");
        Ensure(await GetReplication(ctx, s3, src) is null, "a rejected PutBucketReplication must not apply any configuration");

        var ruleFilterNoDmr = S3Fidelity.Rule("filter-no-dmr", 1, "x/", dstArn);
        ruleFilterNoDmr.DeleteMarkerReplication = null;
        var error3 = await S3Fidelity.Error(() => Put(ruleFilterNoDmr), "expected the call to be rejected, but it succeeded");
        Ensure(error3.Code == "InvalidRequest", $"expected InvalidRequest for Filter without DeleteMarkerReplication, got {Py.Repr(error3.Code)}");
        Ensure(
            error3.Message ==
            "DeleteMarkerReplication must be specified for this version of Cross Region Replication " +
            "configuration schema. Please refer to S3 Developer Guide for more information.",
            error3.Message ?? "None");
        Ensure(await GetReplication(ctx, s3, src) is null, "a rejected PutBucketReplication must not apply any configuration");

        var ruleValid = S3Fidelity.Rule("valid", 1, "", dstArn);
        ruleValid.Destination.ReplicationTime = new ReplicationTime
        {
            Status = ReplicationTimeStatus.Enabled,
            Time = new ReplicationTimeValue { Minutes = 15 },
        };
        ruleValid.Destination.Metrics = new Metrics
        {
            Status = MetricsStatus.Enabled,
            EventThreshold = new ReplicationTimeValue { Minutes = 15 },
        };
        await Put(ruleValid);
        var final = await GetReplication(ctx, s3, src);
        Ensure(final is not null, "a fully valid replication rule must be accepted");
        Ensure(final.Role == roleArn, $"expected Role {roleArn}, got {final.Role}");
        var finalDest = final.Rules[0].Destination;
        Ensure(finalDest.BucketArn == dstArn, $"expected destination {dstArn}, got {finalDest.BucketArn}");
        Ensure(finalDest.ReplicationTime?.Status == ReplicationTimeStatus.Enabled, "final_dest[\"ReplicationTime\"][\"Status\"] == \"Enabled\"");
        Ensure(finalDest.Metrics?.Status == MetricsStatus.Enabled, "final_dest[\"Metrics\"][\"Status\"] == \"Enabled\"");
        Ensure(finalDest.Metrics?.EventThreshold?.Minutes == 15, "final_dest[\"Metrics\"][\"EventThreshold\"][\"Minutes\"] == 15");
    }

    private static async Task<string> CreateTaggedBucket(FidelityContext ctx, IAmazonS3 s3, string prefix)
    {
        var name = await S3Fidelity.CreateBucket(ctx, prefix);
        await s3.PutBucketTaggingAsync(new PutBucketTaggingRequest { BucketName = name, TagSet = [new Tag { Key = "sim2real", Value = Check }] });
        return name;
    }

    /// <summary>
    /// GetBucketReplication, null when no configuration exists, raising on any other error. The .NET SDK
    /// answers the 404 as an empty success, so whether one exists is read off the wire first.
    /// </summary>
    private static async Task<ReplicationConfiguration?> GetReplication(FidelityContext ctx, IAmazonS3 s3, string bucket)
    {
        var (status, code, message) = await S3Fidelity.Raw(ctx, "GET", $"{bucket}?replication");
        if (code == "ReplicationConfigurationNotFoundError")
        {
            return null;
        }

        Ensure(status < 300, $"GetBucketReplication failed with {S3Fidelity.Repr((code, message))}");
        return (await s3.GetBucketReplicationAsync(new GetBucketReplicationRequest { BucketName = bucket })).Configuration;
    }

    /// <summary>
    /// Call probe() (a (code, message) pair) until it stops answering with a freshly-created-role
    /// propagation symptom; still answering one at the deadline raises TimeoutError.
    /// </summary>
    private static async Task<(string? Code, string? Message)> RetryWhileRolePropagates(FidelityContext ctx, Func<Task<(string?, string?)>> probe, double capSeconds = 60.0, double intervalSeconds = 3.0)
    {
        var timeout = Math.Max(1.0, Math.Min(capSeconds, ctx.TimeLeftSeconds - 5.0));
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var (code, message) = await probe();
            if (!RolePropagationCodes.Contains(code))
            {
                return (code, message);
            }

            if (clock.Elapsed.TotalSeconds + intervalSeconds >= timeout)
            {
                throw new TimeoutException($"the role had not propagated within {timeout:F0}s: {Py.Repr(code)} {Py.Repr(message)}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }
    }

    /// <summary>
    /// Call fn() until it succeeds, retrying only on a role-propagation symptom; any other
    /// error propagates, and a symptom still answered at the deadline raises TimeoutError.
    /// </summary>
    private static async Task RetryUntilAccepted(FidelityContext ctx, Func<Task> call, double capSeconds = 60.0, double intervalSeconds = 3.0)
    {
        var timeout = Math.Max(1.0, Math.Min(capSeconds, ctx.TimeLeftSeconds - 5.0));
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(call);
            if (error is null)
            {
                return;
            }

            if (!RolePropagationCodes.Contains(error.Code))
            {
                throw error.Exception;
            }

            if (clock.Elapsed.TotalSeconds + intervalSeconds >= timeout)
            {
                throw new TimeoutException($"the role had not propagated within {timeout:F0}s: {error}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }
    }
}

// Translated from behavioral/s3/check_deep_s3_storage_class_noncurrent_expiration_zero_days_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.deep-s3-storage-class-noncurrent-expiration-zero-days-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeepS3StorageClassNoncurrentExpirationZeroDaysRejected
{
    /// <summary>
    /// NoncurrentVersionExpiration with NoncurrentDays=0 is refused InvalidArgument and leaves the
    /// versioned bucket with NoSuchLifecycleConfiguration, while the identical rule with
    /// NoncurrentDays=1 is stored and read back, pinning the refusal on the day value.
    /// </summary>
    [Test]
    [DisplayName("s3.deep-s3-storage-class-noncurrent-expiration-zero-days-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-dsc-nc-");
        await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest { BucketName = bucket, VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled } });

        static List<LifecycleRule> Rule(int days) =>
        [
            new LifecycleRule
            {
                Id = "noncurrent-expiry",
                Status = LifecycleRuleStatus.Enabled,
                Filter = new LifecycleFilter { Prefix = "logs/" },
                NoncurrentVersionExpiration = new LifecycleRuleNoncurrentVersionExpiration { NoncurrentDays = days },
            },
        ];

        var (code, message) = await S3Fidelity.ClientError(() => s3.PutLifecycleConfigurationAsync(new PutLifecycleConfigurationRequest
        {
            BucketName = bucket,
            Configuration = new LifecycleConfiguration { Rules = Rule(0) },
        }));
        Ensure(code == "InvalidArgument", $"NoncurrentDays=0 returned {Py.Repr(code)}, expected InvalidArgument");
        Ensure(message == "'NoncurrentDays' for NoncurrentVersionExpiration action must be a positive integer", message ?? "None");

        // The .NET SDK answers GetBucketLifecycleConfiguration's 404 as an empty success; the refusal is read off the wire.
        var (absent, absentMessage) = S3Fidelity.Botocore(await S3Fidelity.RawError(ctx, "GET", $"{bucket}?lifecycle", "expected a ClientError, but the call SUCCEEDED"));
        Ensure(
            absent == "NoSuchLifecycleConfiguration",
            $"after the refused PUT GetBucketLifecycleConfiguration returned {Py.Repr(absent)}, expected NoSuchLifecycleConfiguration");
        Ensure(absentMessage == "The lifecycle configuration does not exist", absentMessage ?? "None");

        // Positive control: identical rule, NoncurrentDays=1.
        await s3.PutLifecycleConfigurationAsync(new PutLifecycleConfigurationRequest { BucketName = bucket, Configuration = new LifecycleConfiguration { Rules = Rule(1) } });
        var (stored, storedCode) = await S3Fidelity.Poll(ctx, () => S3Fidelity.LifecycleRules(s3, bucket), got => got.Rules is not null);
        var rules = stored ?? [];
        Ensure(rules.Count == 1, $"NoncurrentDays=1 rule read back as {Py.Repr((object?)stored ?? storedCode)}");
        var nve = rules[0].GetValueOrDefault("NoncurrentVersionExpiration");
        Ensure(
            Py.Equal(nve, new Dictionary<string, object?> { ["NoncurrentDays"] = 1 }),
            $"stored NoncurrentVersionExpiration was {Py.Repr(nve)}");
        Ensure(
            Py.Equal(rules[0].GetValueOrDefault("Filter"), new Dictionary<string, object?> { ["Prefix"] = "logs/" }),
            $"stored Filter was {Py.Repr(rules[0].GetValueOrDefault("Filter"))}");
    }
}

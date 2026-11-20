// Translated from behavioral/s3/check_object_lock_enable_on_versioned_bucket.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.object-lock-enable-on-versioned-bucket</summary>
[Category("AwsEvaluationSuite")]
public sealed class ObjectLockEnableOnVersionedBucket
{
    /// <summary>
    /// Object Lock CAN be enabled after bucket creation once versioning is Enabled; the default
    /// retention rule round-trips and new objects inherit it.
    /// </summary>
    [Test]
    [DisplayName("s3.object-lock-enable-on-versioned-bucket")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var name = FidelityContext.UniqueName("sim2real-behav-lockbkt-");
        var create = new PutBucketRequest { BucketName = name, UseClientRegion = false };
        if (ctx.Region != "us-east-1")
        {
            create.PutBucketConfiguration = new PutBucketConfiguration { LocationConstraint = new BucketLocationConstraint(ctx.Region) };
        }

        await s3.PutBucketAsync(create);
        ctx.OnCleanup(() => S3Fidelity.EmptyAndDeleteObjectLockBucket(s3, name));

        await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = name,
            VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled },
        });

        await s3.PutObjectLockConfigurationAsync(new PutObjectLockConfigurationRequest
        {
            BucketName = name,
            ObjectLockConfiguration = new ObjectLockConfiguration
            {
                ObjectLockEnabled = ObjectLockEnabled.Enabled,
                Rule = new ObjectLockRule { DefaultRetention = new DefaultRetention { Mode = ObjectLockRetentionMode.Governance, Days = 1 } },
            },
        });

        var cfg = (await s3.GetObjectLockConfigurationAsync(new GetObjectLockConfigurationRequest { BucketName = name })).ObjectLockConfiguration;
        Ensure(cfg.ObjectLockEnabled == "Enabled", $"lock not enabled: {Py.Repr(cfg.ObjectLockEnabled?.Value)}");
        var retention = cfg.Rule.DefaultRetention;
        Ensure(retention.Mode == "GOVERNANCE", $"default retention mode not preserved: {Py.Repr(retention.Mode?.Value)}");
        Ensure(retention.Days == 1, $"default retention days not preserved: {Py.Repr(retention.Days)}");

        // objects written afterwards inherit the bucket default retention
        var beforePut = FidelityTime.UtcNow.UtcDateTime;
        var put = await s3.PutObjectAsync(new PutObjectRequest { BucketName = name, Key = "auto-retained.txt", ContentBody = "x" });
        var got = (await s3.GetObjectRetentionAsync(new GetObjectRetentionRequest
        {
            BucketName = name,
            Key = "auto-retained.txt",
            VersionId = put.VersionId,
        })).Retention;
        Ensure(got.Mode == "GOVERNANCE", $"object did not inherit the default retention mode: {Py.Repr(got.Mode?.Value)}");
        Ensure(got.RetainUntilDate is not null, "inherited retention has no RetainUntilDate");
        var retainUntil = S3Fidelity.Utc(got.RetainUntilDate);

        // the bucket default is 1 day: the inherited window must land within that day of the write,
        // not merely be present, or a target that stamps an arbitrary far-future date would pass too.
        var windowStart = beforePut + TimeSpan.FromHours(23);
        var windowEnd = beforePut + TimeSpan.FromHours(25);
        Ensure(
            windowStart <= retainUntil && retainUntil <= windowEnd,
            $"inherited RetainUntilDate {retainUntil:O} is not ~1 day after the write at {beforePut:O}");
    }
}

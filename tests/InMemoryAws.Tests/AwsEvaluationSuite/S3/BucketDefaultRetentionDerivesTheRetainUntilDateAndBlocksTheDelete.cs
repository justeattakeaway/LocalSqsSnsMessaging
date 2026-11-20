// Translated from behavioral/s3/check_bucket_default_retention_derives_the_retain_until_date_and_blocks_the_delete.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.bucket-default-retention-derives-the-retain-until-date-and-blocks-the-delete</summary>
[Category("AwsEvaluationSuite")]
public sealed class BucketDefaultRetentionDerivesTheRetainUntilDateAndBlocksTheDelete
{
    private const string Check = "bucket-default-retention-derives-the-retain-until-date-and-blocks-the-delete";
    private const int Days = 1;
    private const int DayS = 86400;

    // Absorbs clock skew between the client and S3.
    private const double SkewToleranceS = 120.0;
    private const string Key = "locked/inherits-default.bin";
    private static readonly byte[] Body = "object-lock-default-retention-payload"u8.ToArray();

    /// <summary>
    /// An object inherits a GOVERNANCE default retention dated write time plus the period, and a version
    /// delete is refused until BypassGovernanceRetention; a twin bucket with no rule deletes cleanly.
    /// </summary>
    [Test]
    [DisplayName("s3.bucket-default-retention-derives-the-retain-until-date-and-blocks-the-delete")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var retainedBucket = await NewLockedBucket(ctx, s3);
        var controlBucket = await NewLockedBucket(ctx, s3);

        await s3.PutObjectLockConfigurationAsync(new PutObjectLockConfigurationRequest
        {
            BucketName = retainedBucket,
            ObjectLockConfiguration = new ObjectLockConfiguration
            {
                ObjectLockEnabled = ObjectLockEnabled.Enabled,
                Rule = new ObjectLockRule { DefaultRetention = new DefaultRetention { Mode = ObjectLockRetentionMode.Governance, Days = Days } },
            },
        });
        var cfg = (await s3.GetObjectLockConfigurationAsync(new GetObjectLockConfigurationRequest { BucketName = retainedBucket })).ObjectLockConfiguration;
        var rule = cfg.Rule?.DefaultRetention;
        Ensure(
            rule is not null && rule.Mode == "GOVERNANCE" && rule.Days == Days && rule.Years is null,
            $"GetObjectLockConfiguration reports {{'DefaultRetention': {{'Mode': {Py.Repr(rule?.Mode?.Value)}, 'Days': {Py.Repr(rule?.Days)}}}}}; expected {{'DefaultRetention': {{'Mode': 'GOVERNANCE', 'Days': {Days}}}}}");
        var controlCfg = (await s3.GetObjectLockConfigurationAsync(new GetObjectLockConfigurationRequest { BucketName = controlBucket })).ObjectLockConfiguration;
        Ensure(
            controlCfg.Rule?.DefaultRetention is null,
            "the control bucket reports a default-retention rule; it must differ from the retained bucket in exactly this one respect");

        // Bracket the write so the expected date derives from this run's own clock.
        var writtenBefore = Epoch(FidelityTime.UtcNow.UtcDateTime);
        var retainedVersion = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = retainedBucket, Key = Key, InputStream = new MemoryStream(Body) })).VersionId;
        var writtenAfter = Epoch(FidelityTime.UtcNow.UtcDateTime);
        var controlVersion = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = controlBucket, Key = Key, InputStream = new MemoryStream(Body) })).VersionId;

        // The expected date is an interval derived from the measured write time, not a pinned value.
        var retention = (await s3.GetObjectRetentionAsync(new GetObjectRetentionRequest { BucketName = retainedBucket, Key = Key, VersionId = retainedVersion })).Retention;
        Ensure(retention.Mode == "GOVERNANCE", $"the inherited retention mode is {Py.Repr(retention.Mode?.Value)}, expected 'GOVERNANCE'");
        var retainUntil = Epoch(S3Fidelity.Utc(retention.RetainUntilDate));
        var impliedWrite = retainUntil - (Days * DayS);
        Ensure(
            writtenBefore - SkewToleranceS <= impliedWrite && impliedWrite <= writtenAfter + SkewToleranceS,
            $"RetainUntilDate implies a write at epoch {impliedWrite:F0} once the {Days}-day default period is subtracted, but the PutObject happened between {writtenBefore:F0} and {writtenAfter:F0} (tolerance {SkewToleranceS:F0}s) — the retain-until date is not the write time plus the bucket's default period");
        var head = await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = retainedBucket, Key = Key, VersionId = retainedVersion });
        Ensure(
            head.ObjectLockMode == "GOVERNANCE",
            $"HeadObject reports ObjectLockMode {Py.Repr(head.ObjectLockMode?.Value)}, expected 'GOVERNANCE' — GetObjectRetention and HeadObject disagree about the same version");
        Ensure(
            Math.Abs(Epoch(S3Fidelity.Utc(head.ObjectLockRetainUntilDate)) - retainUntil) < 1.0,
            $"HeadObject reports RetainUntilDate {head.ObjectLockRetainUntilDate:O} while GetObjectRetention reports {retention.RetainUntilDate:O}");

        // Twin control: no default rule means no retention at all.
        var (code, message) = await ClientError(() => s3.GetObjectRetentionAsync(new GetObjectRetentionRequest { BucketName = controlBucket, Key = Key, VersionId = controlVersion }));
        Ensure(code == "NoSuchObjectLockConfiguration", "the control object reports a retention even though its bucket has no DefaultRetention rule");
        Ensure(message == "The specified object does not have a ObjectLock configuration", $"unexpected message {Py.Repr(message)}");
        var controlHead = await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = controlBucket, Key = Key, VersionId = controlVersion });
        Ensure(
            controlHead.ObjectLockRetainUntilDate is null,
            $"the control object carries ObjectLockRetainUntilDate {controlHead.ObjectLockRetainUntilDate:O} with no default-retention rule in force");

        (code, message) = await ClientError(() => s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = retainedBucket, Key = Key, VersionId = retainedVersion }));
        Ensure(
            code == "AccessDenied",
            $"deleting version {retainedVersion} under an INHERITED governance retention was not refused with AccessDenied — the bucket default is stored but never consulted");
        Ensure(message == "Access Denied because object protected by object lock.", $"unexpected message {Py.Repr(message)}");
        using (var get = await s3.GetObjectAsync(new GetObjectRequest { BucketName = retainedBucket, Key = Key, VersionId = retainedVersion }))
        {
            Ensure((await S3Fidelity.ReadBody(get)).AsSpan().SequenceEqual(Body), "the retained version's bytes changed even though the delete was refused");
        }

        // The same call on the control bucket succeeds, so the refusal is about the retention.
        await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = controlBucket, Key = Key, VersionId = controlVersion });

        // A HEAD response carries no body, so there is no AWS message to compare.
        (code, _) = await ClientError(() => s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = controlBucket, Key = Key, VersionId = controlVersion }));
        Ensure(code == "404", $"the control version {controlVersion} survived a delete that carried no bypass (got {Py.Repr(code)})");

        // The bypass flag lifts the block; the version is then gone and unusable.
        await s3.DeleteObjectAsync(new DeleteObjectRequest
        {
            BucketName = retainedBucket,
            Key = Key,
            VersionId = retainedVersion,
            BypassGovernanceRetention = true,
        });
        (code, _) = await ClientError(() => s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = retainedBucket, Key = Key, VersionId = retainedVersion }));
        Ensure(code == "404", $"version {retainedVersion} survived a delete carrying BypassGovernanceRetention=True (got {Py.Repr(code)})");
        (code, message) = await ClientError(() => s3.PutObjectRetentionAsync(new PutObjectRetentionRequest
        {
            BucketName = retainedBucket,
            Key = Key,
            VersionId = retainedVersion,
            Retention = new ObjectLockRetention { Mode = ObjectLockRetentionMode.Governance, RetainUntilDate = retention.RetainUntilDate },
        }));
        Ensure(code == "NoSuchVersion", $"PutObjectRetention still mutates the deleted version {retainedVersion} (got {Py.Repr(code)})");
        Ensure(message == "The specified version does not exist.", $"unexpected message {Py.Repr(message)}");
    }

    /// <summary>Create a uniquely-named, tagged, Object-Lock-enabled bucket with teardown registered.</summary>
    private static async Task<string> NewLockedBucket(FidelityContext ctx, AmazonS3Client s3)
    {
        var name = await S3Fidelity.CreateObjectLockBucket(ctx, "sim2real-behav-bkt-", s3);
        await s3.PutBucketTaggingAsync(new PutBucketTaggingRequest
        {
            BucketName = name,
            TagSet = [new Tag { Key = "sim2real:managed", Value = "true" }, new Tag { Key = "sim2real:check", Value = Check }],
        });
        return name;
    }

    /// <summary>
    /// (Error.Code, Error.Message) of fn(), the code falling back to the HTTP status for a code-less
    /// HEAD; raises if fn succeeds.
    /// </summary>
    private static Task<(string? Code, string? Message)> ClientError(Func<Task> fn) =>
        S3Fidelity.ClientError(fn, "the call was expected to fail and it succeeded");

    /// <summary><c>time.time()</c> / <c>datetime.timestamp()</c>: seconds since the epoch.</summary>
    private static double Epoch(DateTime utc) => (utc - DateTime.UnixEpoch).TotalSeconds;
}

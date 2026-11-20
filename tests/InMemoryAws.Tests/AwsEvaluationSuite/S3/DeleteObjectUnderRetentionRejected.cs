// Translated from behavioral/s3/check_delete_object_under_retention_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.delete-object-under-retention-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteObjectUnderRetentionRejected
{
    /// <summary>
    /// DeleteObject of a version under active GOVERNANCE retention is refused with AccessDenied and the
    /// version survives; with BypassGovernanceRetention (or after expiry) the delete succeeds.
    /// </summary>
    [Test]
    [DisplayName("s3.delete-object-under-retention-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        string bucket;
        try
        {
            bucket = await S3Fidelity.CreateObjectLockBucket(ctx, "sim2real-retention-");
        }
        catch (AmazonS3Exception exc)
        {
            throw Fail($"could not create Object-Lock-enabled bucket: {exc.Message}");
        }

        const string key = "retained-object.txt";
        var versionId = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "retained-content" })).VersionId;

        var retainUntil = FidelityTime.UtcNow.UtcDateTime + TimeSpan.FromSeconds(20);
        await s3.PutObjectRetentionAsync(new PutObjectRetentionRequest
        {
            BucketName = bucket,
            Key = key,
            VersionId = versionId,
            Retention = new ObjectLockRetention { Mode = ObjectLockRetentionMode.Governance, RetainUntilDate = retainUntil },
        });
        var retention = (await s3.GetObjectRetentionAsync(new GetObjectRetentionRequest { BucketName = bucket, Key = key, VersionId = versionId })).Retention;
        Ensure(retention.Mode == "GOVERNANCE", $"expected GOVERNANCE mode to round-trip, got {Py.Repr(retention.Mode?.Value)}");

        var err = await ClientError(
            () => s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key, VersionId = versionId }),
            "DeleteObject of a version under active GOVERNANCE retention");
        Ensure(err.Code == "AccessDenied", $"expected AccessDenied under active retention, got {err}");
        Ensure(err.Message == "Access Denied because object protected by object lock.", err.Message ?? "None");

        // the forbidden effect did not happen: version still present, content intact
        using (var get = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key, VersionId = versionId }))
        {
            var body = await S3Fidelity.ReadBody(get);
            Ensure(body.AsSpan().SequenceEqual("retained-content"u8), $"expected the retained version's content untouched, got {Py.Repr(body)}");
        }

        // positive control: an authorized bypass delete succeeds even while retention is active
        bool bypassed;
        try
        {
            await s3.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = bucket,
                Key = key,
                VersionId = versionId,
                BypassGovernanceRetention = true,
            });
            bypassed = true;
        }
        catch (AmazonS3Exception)
        {
            bypassed = false;
        }

        if (!bypassed)
        {
            // fall back: wait out the short retention window, then a plain delete succeeds
            var clock = FidelityTime.StartNew();
            while (true)
            {
                if (clock.Elapsed >= TimeSpan.FromSeconds(30))
                {
                    throw Fail("could not remove retained version even after retention window elapsed");
                }

                if (await FidelityContext.CaptureError(() => s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key, VersionId = versionId })) is null)
                {
                    break;
                }

                await FidelityTime.Delay(TimeSpan.FromSeconds(2));
            }
        }

        var notFound = await ClientError(
            () => s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key, VersionId = versionId }),
            "GetObject of the deleted version");
        Ensure(notFound.Code == "NoSuchVersion", $"expected NoSuchVersion after real delete, got {notFound}");
        Ensure(notFound.Message == "The specified version does not exist.", notFound.Message ?? "None");
    }

    /// <summary>Call fn(); return the ClientError response it raised. A success raises AssertionError.</summary>
    private static async Task<AwsError> ClientError(Func<Task> fn, string what) =>
        await FidelityContext.CaptureError(fn) ?? throw Fail($"{what} succeeded; expected a refusal");
}

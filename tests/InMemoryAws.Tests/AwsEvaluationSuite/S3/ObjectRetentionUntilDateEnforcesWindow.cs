// Translated from behavioral/s3/check_object_retention_until_date_enforces_window.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.object-retention-until-date-enforces-window</summary>
[Category("AwsEvaluationSuite")]
public sealed class ObjectRetentionUntilDateEnforcesWindow
{
    /// <summary>
    /// Within RetainUntilDate, deleting the protected version is AccessDenied and GetObjectRetention
    /// echoes the exact date; after expiry the date still reads back and the same delete succeeds,
    /// leaving the version gone (NoSuchVersion).
    /// </summary>
    [Test]
    [DisplayName("s3.object-retention-until-date-enforces-window")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        // timing:real-time — retention: 12 s https://docs.aws.amazon.com/AmazonS3/latest/userguide/object-lock.html
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateObjectLockBucket(ctx, "sim2real-retain-");
        const string key = "retained.txt";
        var putResp = await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "protected payload" });
        var versionId = putResp.VersionId;

        var retainUntil = FidelityTime.UtcNow.UtcDateTime + TimeSpan.FromSeconds(12);
        await s3.PutObjectRetentionAsync(new PutObjectRetentionRequest
        {
            BucketName = bucket,
            Key = key,
            VersionId = versionId,
            Retention = new ObjectLockRetention { Mode = ObjectLockRetentionMode.Governance, RetainUntilDate = retainUntil },
        });

        // GetObjectRetention reflects exactly the RetainUntilDate we just set (to the second).
        var got = (await s3.GetObjectRetentionAsync(new GetObjectRetentionRequest { BucketName = bucket, Key = key, VersionId = versionId })).Retention;
        Ensure(got.Mode == "GOVERNANCE", $"expected GOVERNANCE mode, got {Py.Repr(got.Mode?.Value)}");
        var delta = Math.Abs((S3Fidelity.Utc(got.RetainUntilDate) - retainUntil).TotalSeconds);
        Ensure(delta < 1.0, $"RetainUntilDate drifted by {delta}s from what was set");

        // While inside the retention window, deleting this exact version is rejected.
        var (deniedCode, deniedMessage) = await Err(() => s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key, VersionId = versionId }));
        Ensure(deniedCode == "AccessDenied", $"expected AccessDenied while retention active, got {Py.Repr(deniedCode)}");
        Ensure(deniedMessage == "Access Denied because object protected by object lock.", $"unexpected message {Py.Repr(deniedMessage)}");

        // ... and the version must still be physically present (the denial had no side effect).
        var stillPresent = await Versions(s3, bucket, key);
        Ensure(stillPresent.Any(v => v.VersionId == versionId), $"version {versionId} missing from list_object_versions after a denied delete");

        // Wait out the window, then confirm the SAME deletion that was denied now succeeds.
        var clock = FidelityTime.StartNew();
        while (FidelityTime.UtcNow.UtcDateTime < retainUntil && clock.Elapsed < TimeSpan.FromSeconds(30))
        {
            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
        }

        Ensure(FidelityTime.UtcNow.UtcDateTime >= retainUntil, "retention window never elapsed within bound");

        // GetObjectRetention still reports the (now-expired) RetainUntilDate -- it isn't cleared
        // just because the clock passed it.
        var postExpiry = (await s3.GetObjectRetentionAsync(new GetObjectRetentionRequest { BucketName = bucket, Key = key, VersionId = versionId })).Retention;
        var postDelta = Math.Abs((S3Fidelity.Utc(postExpiry.RetainUntilDate) - retainUntil).TotalSeconds);
        Ensure(
            postDelta < 1.0,
            $"expired RetainUntilDate should still read back as {retainUntil:O}, drifted by {postDelta}s: got {postExpiry.RetainUntilDate:O}");

        var error = await FidelityContext.CaptureError(() => s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key, VersionId = versionId }));
        if (error is not null)
        {
            // The window is judged on the local clock; a server clock behind it holds the lock a
            // little longer, so the same delete is retried (bounded) while it is still AccessDenied.
            if (error.Code != "AccessDenied")
            {
                throw error.Exception;
            }

            var retryClock = FidelityTime.StartNew();
            while (true)
            {
                await FidelityTime.Delay(TimeSpan.FromSeconds(1));
                var again = await FidelityContext.CaptureError(() => s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key, VersionId = versionId }));
                if (again is null)
                {
                    break;
                }

                if (again.Code != "AccessDenied" || retryClock.Elapsed > TimeSpan.FromSeconds(15))
                {
                    throw again.Exception;
                }
            }
        }

        var remaining = await Versions(s3, bucket, key);
        Ensure(!remaining.Any(v => v.VersionId == versionId), "version survived deletion after retention elapsed");

        // the delete is real, not just absent from a list: an independent op on the dead
        // version id returns the specific not-found code, and a further mutation on it fails too.
        var (goneCode, goneMessage) = await Err(() => s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key, VersionId = versionId }));
        Ensure(goneCode == "NoSuchVersion", $"expected NoSuchVersion reading the deleted version, got {Py.Repr(goneCode)}");
        Ensure(goneMessage == "The specified version does not exist.", $"unexpected message {Py.Repr(goneMessage)}");
        var (retentionGoneCode, retentionGoneMessage) = await Err(() => s3.GetObjectRetentionAsync(new GetObjectRetentionRequest { BucketName = bucket, Key = key, VersionId = versionId }));
        Ensure(retentionGoneCode == "NoSuchVersion", $"expected NoSuchVersion querying retention on the deleted version, got {Py.Repr(retentionGoneCode)}");
        Ensure(retentionGoneMessage == "The specified version does not exist.", $"unexpected message {Py.Repr(retentionGoneMessage)}");
    }

    /// <summary><c>list_object_versions(Bucket=..., Prefix=...)["Versions"]</c>: the versions, without delete markers.</summary>
    private static async Task<List<S3ObjectVersion>> Versions(IAmazonS3 s3, string bucket, string prefix) =>
        [.. ((await s3.ListVersionsAsync(new ListVersionsRequest { BucketName = bucket, Prefix = prefix })).Versions ?? []).Where(v => v.IsDeleteMarker != true)];

    /// <summary>
    /// Call fn(); return its (Error.Code, Error.Message). An unexpected success raises, so accepting a
    /// refused call cannot pass as a code mismatch.
    /// </summary>
    private static Task<(string? Code, string? Message)> Err(Func<Task> fn) =>
        S3Fidelity.ClientError(fn, "expected this call to be refused, but it SUCCEEDED");
}

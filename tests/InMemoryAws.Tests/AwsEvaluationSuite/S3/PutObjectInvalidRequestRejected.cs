// Translated from behavioral/s3/check_put_object_invalid_request_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.put-object-invalid-request-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutObjectInvalidRequestRejected
{
    /// <summary>
    /// PutObject with Object Lock headers on a bucket without Object Lock fails with InvalidRequest
    /// 'Bucket is missing ObjectLockConfiguration' and creates nothing, while on an Object-Lock-enabled
    /// bucket it succeeds and the GOVERNANCE retention reads back.
    /// </summary>
    [Test]
    [DisplayName("s3.put-object-invalid-request-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var retainUntil = FidelityTime.UtcNow.UtcDateTime + TimeSpan.FromDays(1);

        var plainBucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-nolock-");

        const string key = "locked-object.txt";
        var code = await ErrorCode(() => s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = plainBucket,
            Key = key,
            ContentBody = "payload",
            ObjectLockMode = ObjectLockMode.Governance,
            ObjectLockRetainUntilDate = retainUntil,
        }));
        Ensure(code == ("InvalidRequest", "Bucket is missing ObjectLockConfiguration"), $"expected InvalidRequest, got {code}");

        // The rejected write must not have created the object.
        var listing = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = plainBucket });
        Ensure((listing.KeyCount ?? 0) == 0, $"expected empty bucket, got KeyCount={Py.Repr(listing.KeyCount)}");
        Ensure(listing.S3Objects is null or { Count: 0 }, $"expected no Contents, got {Py.Repr(listing.S3Objects?.Select(o => o.Key).ToList())}");

        // Positive control: the same request succeeds on a bucket with Object Lock enabled.
        var lockedBucket = await S3Fidelity.CreateObjectLockBucket(ctx, "sim2real-behav-lock-");

        var putResult = await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = lockedBucket,
            Key = key,
            ContentBody = "payload",
            ObjectLockMode = ObjectLockMode.Governance,
            ObjectLockRetainUntilDate = retainUntil,
        });
        var versionId = putResult.VersionId;
        var head = await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = lockedBucket, Key = key, VersionId = versionId });
        Ensure(head.ObjectLockMode == "GOVERNANCE", $"expected ObjectLockMode=GOVERNANCE, got {Py.Repr(head.ObjectLockMode?.Value)}");
        Ensure(
            S3Fidelity.Utc(head.ObjectLockRetainUntilDate).Date == retainUntil.Date,
            $"expected retain-until date {retainUntil:yyyy-MM-dd}, got {S3Fidelity.Utc(head.ObjectLockRetainUntilDate):yyyy-MM-dd}");
    }

    /// <summary>Call fn(); return its botocore (code, message). An unexpected SUCCESS raises.</summary>
    private static async Task<(string?, string?)> ErrorCode(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn) ?? throw Fail("expected the call to be rejected, but it SUCCEEDED");
        return (error.Code, error.Message ?? string.Empty);
    }
}

// Translated from behavioral/s3/check_multipart_complete_part_too_small_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.multipart-complete-part-too-small-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class MultipartCompletePartTooSmallRejected
{
    /// <summary>
    /// CompleteMultipartUpload must reject a non-final part smaller than 5 MiB with EntityTooSmall,
    /// leaving no object behind -- proven against a positive control showing a correctly-sized
    /// completion on the same bucket succeeds.
    /// </summary>
    [Test]
    [DisplayName("s3.multipart-complete-part-too-small-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");

        // Positive control: a single-part upload has no non-final part, so the 5 MiB minimum
        // does not apply and completion must succeed. Without this, "rejects a too-small
        // non-final part" would be indistinguishable from "rejects every CompleteMultipartUpload".
        const string controlKey = "mpu-control.bin";
        var controlUploadId = (await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = controlKey })).UploadId;
        ctx.OnCleanup(S3Fidelity.AbortQuietly(s3, bucket, controlKey, controlUploadId));
        var controlEtag = (await s3.UploadPartAsync(new UploadPartRequest { BucketName = bucket, Key = controlKey, PartNumber = 1, UploadId = controlUploadId, InputStream = new MemoryStream(S3Fidelity.Repeat('c', 1024)) })).ETag;
        await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest { BucketName = bucket, Key = controlKey, UploadId = controlUploadId, PartETags = [S3Fidelity.Part(1, controlEtag)] });
        var controlSize = (await s3.GetObjectMetadataAsync(bucket, controlKey)).ContentLength;
        Ensure(controlSize == 1024, $"positive control: expected a 1024-byte object, got {controlSize}");

        // Negative: a non-final part smaller than 5 MiB must be rejected.
        const string key = "mpu-toosmall.bin";
        var uploadId = (await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = key })).UploadId;
        ctx.OnCleanup(S3Fidelity.AbortQuietly(s3, bucket, key, uploadId));
        var e1 = (await s3.UploadPartAsync(new UploadPartRequest { BucketName = bucket, Key = key, PartNumber = 1, UploadId = uploadId, InputStream = new MemoryStream(S3Fidelity.Repeat('a', 1024)) })).ETag;
        var e2 = (await s3.UploadPartAsync(new UploadPartRequest { BucketName = bucket, Key = key, PartNumber = 2, UploadId = uploadId, InputStream = new MemoryStream(S3Fidelity.Repeat('b', 1024)) })).ETag;

        var (code, message) = await S3Fidelity.ErrorPair(() => s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket,
            Key = key,
            UploadId = uploadId,
            PartETags = [S3Fidelity.Part(1, e1), S3Fidelity.Part(2, e2)],
        }));
        Ensure(code == "EntityTooSmall", $"expected EntityTooSmall for a 1 KiB non-final part, got {Py.Repr(code)}");
        Ensure(message == "Your proposed upload is smaller than the minimum allowed size", message ?? "None");

        // the object must NOT have been materialised
        // HeadObject's 404 carries no body, so there is no AWS message to assert.
        var (notFound, _) = await S3Fidelity.HeadErrorPair(() => s3.GetObjectMetadataAsync(bucket, key));
        Ensure(notFound == "404", $"expected the rejected completion to leave no object, got {Py.Repr(notFound)}");
    }
}

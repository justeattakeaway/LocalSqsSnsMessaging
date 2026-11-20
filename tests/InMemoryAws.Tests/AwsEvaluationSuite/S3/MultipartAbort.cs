// Translated from behavioral/s3/check_multipart_abort.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.multipart-abort</summary>
[Category("AwsEvaluationSuite")]
public sealed class MultipartAbort
{
    private const string ErrNoSuchUpload = "NoSuchUpload";

    /// <summary>AbortMultipartUpload prevents completion and cleans up.</summary>
    [Test]
    [DisplayName("s3.multipart-abort")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateTaggedBucket(ctx, "sim2real-behav", "true");
        const string key = "multipart/aborted.bin";
        var mpu = await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = key });
        var uploadId = mpu.UploadId;

        await s3.UploadPartAsync(new UploadPartRequest
        {
            BucketName = bucket,
            Key = key,
            UploadId = uploadId,
            PartNumber = 1,
            InputStream = new MemoryStream(S3Fidelity.Repeat('X', 5 * 1024 * 1024)),
        });
        await s3.AbortMultipartUploadAsync(bucket, key, uploadId);

        // ListParts after abort should fail with NoSuchUpload: the upload id is gone, not just idle.
        var (code, message) = await S3Fidelity.ErrorPair(() => s3.ListPartsAsync(new ListPartsRequest { BucketName = bucket, Key = key, UploadId = uploadId }));
        Ensure(code == ErrNoSuchUpload, $"expected {ErrNoSuchUpload}, got {Py.Repr(code)}");
        Ensure(message == "The specified upload does not exist. The upload ID may be invalid, or the upload may have been aborted or completed.", message ?? "None");

        // A mutation on the same dead upload id must also fail, not just a read: the id is unusable,
        // not merely unlisted.
        (code, message) = await S3Fidelity.ErrorPair(() => s3.UploadPartAsync(new UploadPartRequest
        {
            BucketName = bucket,
            Key = key,
            UploadId = uploadId,
            PartNumber = 2,
            InputStream = new MemoryStream(S3Fidelity.Repeat('Y', 5 * 1024 * 1024)),
        }));
        Ensure(code == ErrNoSuchUpload, $"expected {ErrNoSuchUpload} on UploadPart, got {Py.Repr(code)}");
        Ensure(message == "The specified upload does not exist. The upload ID may be invalid, or the upload may have been aborted or completed.", message ?? "None");
    }
}

// Translated from behavioral/s3/check_abort_multipart_nosuchupload_after_complete.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.abort-multipart-nosuchupload-after-complete</summary>
[Category("AwsEvaluationSuite")]
public sealed class AbortMultipartNosuchuploadAfterComplete
{
    /// <summary>
    /// AbortMultipartUpload after CompleteMultipartUpload succeeds as a no-op and leaves the completed
    /// object intact with its full ContentLength.
    /// </summary>
    [Test]
    [DisplayName("s3.abort-multipart-nosuchupload-after-complete")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx);
        const string key = "mpu-object.bin";
        var body = new byte[1024];
        Array.Fill(body, (byte)'x');

        var create = await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = key });
        var uploadId = create.UploadId;

        var part = await s3.UploadPartAsync(new UploadPartRequest
        {
            BucketName = bucket,
            Key = key,
            PartNumber = 1,
            UploadId = uploadId,
            InputStream = new MemoryStream(body),
        });
        await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket,
            Key = key,
            UploadId = uploadId,
            PartETags = [new PartETag { ETag = part.ETag, PartNumber = 1 }],
        });
        ctx.OnCleanup(() => s3.DeleteObjectAsync(bucket, key));

        // The completed object really exists now.
        await s3.GetObjectMetadataAsync(bucket, key);

        // Real S3: aborting an upload id that has already been finalized by
        // CompleteMultipartUpload succeeds silently (no NoSuchUpload) rather than
        // erroring -- it is a genuine no-op, not a resurrection of the abort path.
        await ExpectSuccess(
            () => s3.AbortMultipartUploadAsync(new AbortMultipartUploadRequest { BucketName = bucket, Key = key, UploadId = uploadId }),
            "AbortMultipartUpload on an already-completed upload id");

        // The already-completed object must be completely unaffected by the abort.
        var stillThere = await s3.GetObjectMetadataAsync(bucket, key);
        Ensure(
            stillThere.ContentLength == body.Length,
            "abort of an already-completed multipart upload must not disturb the finalized object");
    }

    /// <summary>
    /// Call fn(); a ClientError raises AssertionError naming its code, since the claim is that the
    /// call succeeds as a no-op.
    /// </summary>
    private static async Task ExpectSuccess(Func<Task> fn, string what)
    {
        if (await FidelityContext.CaptureError(fn) is { } err)
        {
            throw Fail($"expected {what} to succeed as a no-op, got {Py.Repr(err.Code)}: {Py.Repr(err.Message)}");
        }
    }
}

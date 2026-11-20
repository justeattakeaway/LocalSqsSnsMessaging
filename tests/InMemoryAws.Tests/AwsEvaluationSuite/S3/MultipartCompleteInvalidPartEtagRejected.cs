// Translated from behavioral/s3/check_multipart_complete_invalid_part_etag_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.multipart-complete-invalid-part-etag-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class MultipartCompleteInvalidPartEtagRejected
{
    /// <summary>
    /// CompleteMultipartUpload with a mismatched part ETag must fail with InvalidPart
    /// (overly-permissive boundary); the rejected call must not materialise the object, and the
    /// upload must still be abortable afterward -- and gone-and-unusable once aborted.
    /// </summary>
    [Test]
    [DisplayName("s3.multipart-complete-invalid-part-etag-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        const string key = "mpu-badpart.bin";
        var uploadId = (await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = key })).UploadId;
        await s3.UploadPartAsync(new UploadPartRequest { BucketName = bucket, Key = key, PartNumber = 1, UploadId = uploadId, InputStream = new MemoryStream(S3Fidelity.Repeat('y', 1024)) });

        var (code, message) = await S3Fidelity.ErrorPair(() => s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket,
            Key = key,
            UploadId = uploadId,
            PartETags = [S3Fidelity.Part(1, "\"00000000000000000000000000000000\"")],
        }));
        Ensure(code == "InvalidPart", $"expected InvalidPart, got {Py.Repr(code)}");
        Ensure(message == "One or more of the specified parts could not be found.  The part may not have been uploaded, or the specified entity tag may not match the part's entity tag.", message ?? "None");

        // the rejected completion must not have materialised the object
        // HeadObject's 404 carries no body, so there is no AWS message to assert.
        var (headCode, _) = await S3Fidelity.HeadErrorPair(() => s3.GetObjectMetadataAsync(bucket, key));
        Ensure(headCode == "404", $"object must not exist after a rejected completion, got {Py.Repr(headCode)}");

        // upload must still be abortable/cleanup-able after the failed completion
        await s3.AbortMultipartUploadAsync(bucket, key, uploadId);

        // ...and gone-and-unusable once aborted
        var (goneCode, goneMessage) = await S3Fidelity.ErrorPair(() => s3.ListPartsAsync(new ListPartsRequest { BucketName = bucket, Key = key, UploadId = uploadId }));
        Ensure(goneCode == "NoSuchUpload", $"expected NoSuchUpload after abort, got {Py.Repr(goneCode)}");
        Ensure(goneMessage == "The specified upload does not exist. The upload ID may be invalid, or the upload may have been aborted or completed.", goneMessage ?? "None");
    }
}

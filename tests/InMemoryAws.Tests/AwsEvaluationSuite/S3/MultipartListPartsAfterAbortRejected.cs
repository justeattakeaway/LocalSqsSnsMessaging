// Translated from behavioral/s3/check_multipart_list_parts_after_abort_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.multipart-list-parts-after-abort-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class MultipartListPartsAfterAbortRejected
{
    /// <summary>
    /// ListParts on an aborted UploadId fails with NoSuchUpload and its exact message, and so does
    /// UploadPart on it, while the same ListParts on the live upload returned the uploaded part's ETag.
    /// </summary>
    [Test]
    [DisplayName("s3.multipart-list-parts-after-abort-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        const string key = "parts-aborted-obj";
        var uploadId = (await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = key })).UploadId;

        var partBody = S3Fidelity.Repeat('P', 1024);
        var part = await s3.UploadPartAsync(new UploadPartRequest { BucketName = bucket, Key = key, UploadId = uploadId, PartNumber = 1, InputStream = new MemoryStream(partBody) });

        var live = await s3.ListPartsAsync(new ListPartsRequest { BucketName = bucket, Key = key, UploadId = uploadId });
        var liveParts = live.Parts ?? [];
        Ensure(liveParts.Count == 1, $"expected 1 live part before abort, got {liveParts.Count}");
        Ensure(
            liveParts[0].ETag == part.ETag,
            $"live ListParts ETag {Py.Repr(liveParts[0].ETag)} must match UploadPart's {Py.Repr(part.ETag)}");

        await s3.AbortMultipartUploadAsync(bucket, key, uploadId);

        var (code, message) = await S3Fidelity.ErrorPair(() => s3.ListPartsAsync(new ListPartsRequest { BucketName = bucket, Key = key, UploadId = uploadId }));
        Ensure(code == "NoSuchUpload", $"expected NoSuchUpload, got {Py.Repr(code)}");
        Ensure(message == "The specified upload does not exist. The upload ID may be invalid, or the upload may have been aborted or completed.", message ?? "None");

        var (mutateCode, mutateMessage) = await S3Fidelity.ErrorPair(() => s3.UploadPartAsync(new UploadPartRequest
        {
            BucketName = bucket,
            Key = key,
            UploadId = uploadId,
            PartNumber = 2,
            InputStream = new MemoryStream("x"u8.ToArray()),
        }));
        Ensure(mutateCode == "NoSuchUpload", $"expected NoSuchUpload for UploadPart against an aborted upload, got {Py.Repr(mutateCode)}");
        Ensure(mutateMessage == "The specified upload does not exist. The upload ID may be invalid, or the upload may have been aborted or completed.", mutateMessage ?? "None");
    }
}

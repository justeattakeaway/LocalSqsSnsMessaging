// Translated from behavioral/s3/check_multipart_upload_complete_wrong_part_order_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.multipart-upload-complete-wrong-part-order-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class MultipartUploadCompleteWrongPartOrderRejected
{
    /// <summary>
    /// CompleteMultipartUpload with a non-ascending Parts list is rejected with InvalidPartOrder and
    /// its exact message and leaves the upload in progress; the same parts in ascending order complete
    /// it with the full size and ListMultipartUploads omits the upload.
    /// </summary>
    [Test]
    [DisplayName("s3.multipart-upload-complete-wrong-part-order-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-mpuneg-");

        const string key = "bad-order-object.bin";
        var created = await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = key });
        var uploadId = created.UploadId;
        ctx.OnCleanup(S3Fidelity.AbortQuietly(s3, bucket, key, uploadId));

        const int part1Size = 5 * 1024 * 1024;
        const int part2Size = 1 * 1024 * 1024;
        var part1 = await s3.UploadPartAsync(new UploadPartRequest { BucketName = bucket, Key = key, PartNumber = 1, UploadId = uploadId, InputStream = new MemoryStream(S3Fidelity.RandomBytes(part1Size)) });
        var part2 = await s3.UploadPartAsync(new UploadPartRequest { BucketName = bucket, Key = key, PartNumber = 2, UploadId = uploadId, InputStream = new MemoryStream(S3Fidelity.RandomBytes(part2Size)) });

        var (code, message) = await S3Fidelity.ErrorPair(
            () => S3Fidelity.CompleteInListedOrder(ctx, bucket, key, uploadId, [S3Fidelity.Part(2, part2.ETag), S3Fidelity.Part(1, part1.ETag)]),
            "expected a ClientError but the call succeeded");
        Ensure(code == "InvalidPartOrder", $"expected InvalidPartOrder, got {code}");
        Ensure(message == "The list of parts was not in ascending order. Parts must be ordered by part number.", message ?? "None");

        // the upload must still be open (rejected completion did not finalize it)
        var listed = await s3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = bucket });
        var uploadIds = (listed.MultipartUploads ?? []).Select(u => u.UploadId).ToList();
        Ensure(uploadIds.Contains(uploadId), "rejected completion should leave the upload in progress");

        // positive control: the same parts, resubmitted in ascending order, must complete —
        // otherwise a target that rejects every CompleteMultipartUpload call would pass above
        var completed = await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket,
            Key = key,
            UploadId = uploadId,
            PartETags = [S3Fidelity.Part(1, part1.ETag), S3Fidelity.Part(2, part2.ETag)],
        });
        Ensure(completed.Key == key, $"expected completed key {Py.Repr(key)}, got {Py.Repr(completed.Key)}");

        // terminal assertions are independent reads at the destination, not the mutating call's own
        // response
        var head = await s3.GetObjectMetadataAsync(bucket, key);
        const int expectedSize = part1Size + part2Size;
        Ensure(head.ContentLength == expectedSize, $"expected assembled object size {expectedSize}, got {head.ContentLength}");
        var listedAfter = await s3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = bucket });
        var uploadIdsAfter = (listedAfter.MultipartUploads ?? []).Select(u => u.UploadId).ToList();
        Ensure(!uploadIdsAfter.Contains(uploadId), "completed upload should no longer be in progress");
    }
}

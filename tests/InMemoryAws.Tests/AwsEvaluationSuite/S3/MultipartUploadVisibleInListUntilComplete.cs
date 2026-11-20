// Translated from behavioral/s3/check_multipart_upload_visible_in_list_until_complete.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.multipart-upload-visible-in-list-until-complete</summary>
[Category("AwsEvaluationSuite")]
public sealed class MultipartUploadVisibleInListUntilComplete
{
    /// <summary>An in-progress multipart upload appears in ListMultipartUploads and disappears once completed.</summary>
    [Test]
    [DisplayName("s3.multipart-upload-visible-in-list-until-complete")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-mpu-");

        const string key = "multipart-object.bin";
        var created = await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = key });
        var uploadId = created.UploadId;
        ctx.OnCleanup(S3Fidelity.AbortQuietly(s3, bucket, key, uploadId));

        var listedDuring = await s3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = bucket });
        var inProgress = (listedDuring.MultipartUploads ?? []).Select(u => (u.Key, u.UploadId)).ToHashSet();
        Ensure(inProgress.Contains((key, uploadId)), $"expected in-progress upload in {Py.Repr(inProgress.Select(p => $"({p.Key}, {p.UploadId})"))}");

        var body = S3Fidelity.Repeat('a', 5 * 1024 * 1024);
        var part = await s3.UploadPartAsync(new UploadPartRequest
        {
            BucketName = bucket,
            Key = key,
            PartNumber = 1,
            UploadId = uploadId,
            InputStream = new MemoryStream(body),
        });
        var etag = part.ETag;

        // part is visible via ListParts while the upload is still open
        var parts = (await s3.ListPartsAsync(new ListPartsRequest { BucketName = bucket, Key = key, UploadId = uploadId })).Parts ?? [];
        Ensure(
            parts.Count == 1 && parts[0].ETag == etag && parts[0].PartNumber == 1,
            $"expected one part (PartNumber=1, ETag={Py.Repr(etag)}), got {parts.Count} parts");

        await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket,
            Key = key,
            UploadId = uploadId,
            PartETags = [S3Fidelity.Part(1, etag)],
        });

        var listedAfter = await s3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = bucket });
        var after = (listedAfter.MultipartUploads ?? []).Select(u => (u.Key, u.UploadId)).ToHashSet();
        Ensure(!after.Contains((key, uploadId)), $"completed upload still listed: {Py.Repr(after.Select(p => $"({p.Key}, {p.UploadId})"))}");

        using var obj = await s3.GetObjectAsync(bucket, key);
        var gotBody = await S3Fidelity.ReadBody(obj);
        Ensure(gotBody.AsSpan().SequenceEqual(body), $"expected {body.Length} bytes of 'a', got {gotBody.Length} bytes");
        Ensure(obj.ContentLength == body.Length, $"expected ContentLength={body.Length}, got {obj.ContentLength}");
    }
}

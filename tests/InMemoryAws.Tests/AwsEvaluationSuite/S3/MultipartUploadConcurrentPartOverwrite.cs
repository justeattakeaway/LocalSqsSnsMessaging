// Translated from behavioral/s3/check_multipart_upload_concurrent_part_overwrite.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.multipart-upload-concurrent-part-overwrite</summary>
[Category("AwsEvaluationSuite")]
public sealed class MultipartUploadConcurrentPartOverwrite
{
    /// <summary>
    /// Re-uploading a PartNumber replaces its stored ETag and size in ListParts; completing with the
    /// stale ETag fails with InvalidPart and its exact message and creates no object, and completing
    /// with the latest ETag yields only the latest part's bytes.
    /// </summary>
    [Test]
    [DisplayName("s3.multipart-upload-concurrent-part-overwrite")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-mpuoverwrite-");

        const string key = "assembled-object.bin";
        var uploadId = (await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = key })).UploadId;
        ctx.OnCleanup(S3Fidelity.AbortQuietly(s3, bucket, key, uploadId));

        var partV1 = S3Fidelity.Repeat('A', 64 * 1024);
        var partV2 = S3Fidelity.Repeat('B', 128 * 1024);

        var etagV1 = (await s3.UploadPartAsync(new UploadPartRequest { BucketName = bucket, Key = key, PartNumber = 1, UploadId = uploadId, InputStream = new MemoryStream(partV1) })).ETag;
        var etagV2 = (await s3.UploadPartAsync(new UploadPartRequest { BucketName = bucket, Key = key, PartNumber = 1, UploadId = uploadId, InputStream = new MemoryStream(partV2) })).ETag;

        Ensure(etagV1 != etagV2, "re-uploading the same part number should change its ETag");

        // ListParts reflects only the latest overwrite for that part number, not both
        var parts = (await s3.ListPartsAsync(new ListPartsRequest { BucketName = bucket, Key = key, UploadId = uploadId })).Parts ?? [];
        Ensure(parts.Count == 1, $"expected exactly one part after overwrite, got {parts.Count}");
        Ensure(parts[0].PartNumber == 1, $"expected PartNumber 1, got {parts[0].PartNumber}");
        Ensure(parts[0].ETag == etagV2, $"expected ListParts ETag to be the latest {etagV2}, got {parts[0].ETag}");
        Ensure(parts[0].Size == partV2.Length, $"expected ListParts Size {partV2.Length}, got {parts[0].Size}");

        // completing against the now-stale first ETag is rejected
        var (staleErr, staleMessage) = await S3Fidelity.ErrorPair(() => s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket,
            Key = key,
            UploadId = uploadId,
            PartETags = [S3Fidelity.Part(1, etagV1)],
        }));
        Ensure(staleErr == "InvalidPart", $"expected InvalidPart for stale ETag, got {staleErr}");
        Ensure(staleMessage == "One or more of the specified parts could not be found.  The part may not have been uploaded, or the specified entity tag may not match the part's entity tag.", staleMessage ?? "None");

        // the rejected completion must not have assembled the object -- independent read confirms absence
        var (absentErr, absentMessage) = await S3Fidelity.ErrorPair(() => s3.GetObjectAsync(bucket, key));
        Ensure(absentErr == "NoSuchKey", $"object must not exist after the stale-ETag completion was rejected, got {Py.Repr(absentErr)}");
        Ensure(absentMessage == "The specified key does not exist.", absentMessage ?? "None");

        // completing against the latest ETag succeeds and assembles only the v2 bytes
        await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket,
            Key = key,
            UploadId = uploadId,
            PartETags = [S3Fidelity.Part(1, etagV2)],
        });
        var body = await S3Fidelity.GetBytes(s3, bucket, key);
        Ensure(body.AsSpan().SequenceEqual(partV2), $"expected assembled object to contain only v2 bytes, got {body.Length} bytes of other content");
        Ensure(body.Length == partV2.Length, $"expected object length {partV2.Length}, got {body.Length}");
    }
}

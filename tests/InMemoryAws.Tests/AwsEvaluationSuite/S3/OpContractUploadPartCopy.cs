// Translated from behavioral/s3/check_op_contract_upload_part_copy.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.op-contract-uploadPartCopy</summary>
[Category("AwsEvaluationSuite")]
public sealed class OpContractUploadPartCopy
{
    private const int MinPartSize = 5 * 1024 * 1024;
    private static readonly (string, string) NoSuchUpload = ("NoSuchUpload", "The specified upload does not exist. The upload ID may be invalid, or the upload may have been aborted or completed.");

    /// <summary>
    /// UploadPartCopy: success-path contract (copied part is byte-accurate and
    /// assembles correctly via CompleteMultipartUpload) plus a NoSuchUpload error
    /// boundary for a copy against a nonexistent/aborted upload id.
    /// </summary>
    [Test]
    [DisplayName("s3.op-contract-uploadPartCopy")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-uploadpartcopy-");

        // --- set up a source object big enough to serve as a valid non-final part ---
        const string sourceKey = "source-object";
        byte[] sourceBody = [.. S3Fidelity.RandomBytes(MinPartSize), .. "source-tail-marker"u8];
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = sourceKey, InputStream = new MemoryStream(sourceBody) });
        ctx.OnCleanup(() => s3.DeleteObjectAsync(bucket, sourceKey));

        const string destKey = "dest-object";

        // === success path ===
        var createResp = await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = destKey });
        var uploadId = createResp.UploadId;
        ctx.OnCleanup(S3Fidelity.AbortQuietly(s3, bucket, destKey, uploadId));

        // Part 1: copy the first MIN_PART_SIZE bytes from the source via UploadPartCopy.
        var part1 = await s3.CopyPartAsync(new CopyPartRequest
        {
            DestinationBucket = bucket,
            DestinationKey = destKey,
            UploadId = uploadId,
            PartNumber = 1,
            SourceBucket = bucket,
            SourceKey = sourceKey,
            FirstByte = 0,
            LastByte = MinPartSize - 1,
        });
        Ensure(!string.IsNullOrEmpty(part1.ETag), "CopyPartResult missing ETag");
        Ensure(part1.LastModified is not null, "CopyPartResult missing LastModified");
        var part1Etag = part1.ETag;

        // Part 2: a small final part uploaded directly (covers the tail marker bytes).
        var tailBytes = sourceBody[MinPartSize..];
        var part2 = await s3.UploadPartAsync(new UploadPartRequest { BucketName = bucket, Key = destKey, UploadId = uploadId, PartNumber = 2, InputStream = new MemoryStream(tailBytes) });
        var part2Etag = part2.ETag;

        var complete = await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket,
            Key = destKey,
            UploadId = uploadId,
            PartETags = [S3Fidelity.Part(1, part1Etag), S3Fidelity.Part(2, part2Etag)],
        });
        Ensure(complete.Key == destKey, $"CompleteMultipartUpload response Key mismatch: expected {Py.Repr(destKey)}, got {Py.Repr(complete.Key)}");
        ctx.OnCleanup(() => s3.DeleteObjectAsync(bucket, destKey));

        // Assert the assembled object is byte-for-byte identical to the source.
        var fetched = await S3Fidelity.GetBytes(s3, bucket, destKey);
        Ensure(
            fetched.AsSpan().SequenceEqual(sourceBody),
            $"assembled object ({fetched.Length} bytes) does not match source ({sourceBody.Length} bytes) after UploadPartCopy + UploadPart + Complete");

        // ListParts on a now-completed upload should fail (upload id retired).
        var code = await S3Fidelity.ErrorPair(() => s3.ListPartsAsync(new ListPartsRequest { BucketName = bucket, Key = destKey, UploadId = uploadId }));
        Ensure(code == NoSuchUpload, $"expected NoSuchUpload after completion, got {code}");

        // === error boundary: UploadPartCopy against a bogus/nonexistent UploadId ===
        const string bogusUploadId = "bogus-upload-id-does-not-exist-00000000000000000000000000000000";
        code = await S3Fidelity.ErrorPair(() => s3.CopyPartAsync(new CopyPartRequest
        {
            DestinationBucket = bucket,
            DestinationKey = destKey,
            UploadId = bogusUploadId,
            PartNumber = 1,
            SourceBucket = bucket,
            SourceKey = sourceKey,
            FirstByte = 0,
            LastByte = MinPartSize - 1,
        }));
        Ensure(code == NoSuchUpload, $"expected NoSuchUpload for bogus upload id, got {code}");
    }
}

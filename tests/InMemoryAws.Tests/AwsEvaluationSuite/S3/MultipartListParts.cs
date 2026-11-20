// Translated from behavioral/s3/check_multipart_list_parts.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.multipart-list-parts</summary>
[Category("AwsEvaluationSuite")]
public sealed class MultipartListParts
{
    /// <summary>ListParts returns the uploaded parts with correct ETags and sizes.</summary>
    [Test]
    [DisplayName("s3.multipart-list-parts")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        const string key = "multipart/list-parts.bin";
        var mpu = await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = key });
        var uploadId = mpu.UploadId;

        var partBody = S3Fidelity.Repeat('P', 5 * 1024 * 1024);
        var r1 = await s3.UploadPartAsync(new UploadPartRequest
        {
            BucketName = bucket,
            Key = key,
            UploadId = uploadId,
            PartNumber = 1,
            InputStream = new MemoryStream(partBody),
        });

        var expectedEtag = $"\"{S3Fidelity.Md5Hex(partBody)}\"";
        Ensure(r1.ETag == expectedEtag, $"upload_part ETag {Py.Repr(r1.ETag)} != recomputed MD5 {Py.Repr(expectedEtag)}");

        var resp = await s3.ListPartsAsync(new ListPartsRequest { BucketName = bucket, Key = key, UploadId = uploadId });
        var parts = resp.Parts ?? [];
        Ensure(parts.Count == 1, $"expected 1 part, got {parts.Count}");
        Ensure(parts[0].PartNumber == 1, $"expected PartNumber 1, got {parts[0].PartNumber}");
        Ensure(parts[0].ETag == expectedEtag, $"list_parts ETag {Py.Repr(parts[0].ETag)} != recomputed MD5 {Py.Repr(expectedEtag)}");
        Ensure(parts[0].Size == partBody.Length, $"expected Size {partBody.Length}, got {parts[0].Size}");

        // Cleanup
        await s3.AbortMultipartUploadAsync(bucket, key, uploadId);
    }
}

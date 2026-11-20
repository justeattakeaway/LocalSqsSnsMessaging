// Translated from behavioral/s3/check_multipart_etag_format.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.RegularExpressions;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.multipart-etag-format</summary>
[Category("AwsEvaluationSuite")]
public sealed class MultipartEtagFormat
{
    /// <summary>Completed multipart object ETag contains a dash and part count (e.g. '...-2').</summary>
    [Test]
    [DisplayName("s3.multipart-etag-format")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        const string key = "multipart/etag-format.bin";
        var partBody = S3Fidelity.Repeat('E', 5 * 1024 * 1024);

        var mpu = await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = key });
        var uploadId = mpu.UploadId;

        var r1 = await s3.UploadPartAsync(new UploadPartRequest { BucketName = bucket, Key = key, UploadId = uploadId, PartNumber = 1, InputStream = new MemoryStream(partBody) });
        var r2 = await s3.UploadPartAsync(new UploadPartRequest { BucketName = bucket, Key = key, UploadId = uploadId, PartNumber = 2, InputStream = new MemoryStream(partBody) });

        await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket,
            Key = key,
            UploadId = uploadId,
            PartETags = [S3Fidelity.Part(1, r1.ETag), S3Fidelity.Part(2, r2.ETag)],
        });
        var head = await s3.GetObjectMetadataAsync(bucket, key);
        var etag = head.ETag.Trim('"');
        // Multipart ETags have format: <32-hex-digest>-<part_count>. A bare substring test
        // ("-2" in etag) would wrongly accept "-20".."-29", so match the whole thing.
        var m = Regex.Match(etag, "^[0-9a-f]{32}-(\\d+)$");
        Ensure(m.Success, $"multipart ETag should be '<32-hex-digest>-<part_count>', got: {etag}");
        Ensure(m.Groups[1].Value == "2", $"multipart ETag part count should be exactly '2', got: {etag}");
    }
}

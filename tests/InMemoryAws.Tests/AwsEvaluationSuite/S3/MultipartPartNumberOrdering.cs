// Translated from behavioral/s3/check_multipart_part_number_ordering.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.multipart-part-number-ordering</summary>
[Category("AwsEvaluationSuite")]
public sealed class MultipartPartNumberOrdering
{
    /// <summary>S3 multipart minimum part size (except last part).</summary>
    private const int MinPartSize = 5 * 1024 * 1024;

    /// <summary>
    /// Parts uploaded out of numeric order are assembled by ascending PartNumber. On
    /// CompleteMultipartUpload the assembled body must equal part1+part2 (numeric order) even though
    /// part2 was physically uploaded to S3 first.
    /// </summary>
    [Test]
    [DisplayName("s3.multipart-part-number-ordering")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        const string key = "mp-partorder-obj";
        var uploadId = (await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = key })).UploadId;

        var part1Data = S3Fidelity.RandomBytes(MinPartSize);
        var part2Data = S3Fidelity.RandomBytes(1024);

        // Upload part 2 first, then part 1 -- deliberately out of numeric order.
        var p2 = await s3.UploadPartAsync(new UploadPartRequest { BucketName = bucket, Key = key, PartNumber = 2, UploadId = uploadId, InputStream = new MemoryStream(part2Data) });
        var p1 = await s3.UploadPartAsync(new UploadPartRequest { BucketName = bucket, Key = key, PartNumber = 1, UploadId = uploadId, InputStream = new MemoryStream(part1Data) });

        await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket,
            Key = key,
            UploadId = uploadId,
            PartETags = [S3Fidelity.Part(1, p1.ETag), S3Fidelity.Part(2, p2.ETag)],
        });

        var body = await S3Fidelity.GetBytes(s3, bucket, key);
        Ensure(
            body.AsSpan().SequenceEqual([.. part1Data, .. part2Data]),
            "assembled object must be ordered by ascending PartNumber, not by upload arrival order");
        Ensure(body.Length == MinPartSize + 1024, $"expected assembled size {MinPartSize + 1024}, got {body.Length}");
    }
}

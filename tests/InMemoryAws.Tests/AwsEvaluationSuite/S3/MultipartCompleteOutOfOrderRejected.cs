// Translated from behavioral/s3/check_multipart_complete_out_of_order_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.multipart-complete-out-of-order-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class MultipartCompleteOutOfOrderRejected
{
    /// <summary>S3 multipart minimum part size (except last part).</summary>
    private const int MinPartSize = 5 * 1024 * 1024;

    /// <summary>
    /// CompleteMultipartUpload with parts in descending PartNumber order is rejected with
    /// InvalidPartOrder and its exact message, the uploaded parts stay listed, and the same upload
    /// completes as part1 + part2 once the list is ascending.
    /// </summary>
    [Test]
    [DisplayName("s3.multipart-complete-out-of-order-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        const string key = "mp-badorder-obj";
        var uploadId = (await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = key })).UploadId;

        var part1Body = S3Fidelity.RandomBytes(MinPartSize);
        var part2Body = S3Fidelity.Repeat('y', 1024);
        var completed = false;
        try
        {
            var p1 = await s3.UploadPartAsync(new UploadPartRequest { BucketName = bucket, Key = key, PartNumber = 1, UploadId = uploadId, InputStream = new MemoryStream(part1Body) });
            var p2 = await s3.UploadPartAsync(new UploadPartRequest { BucketName = bucket, Key = key, PartNumber = 2, UploadId = uploadId, InputStream = new MemoryStream(part2Body) });
            var (code, message) = await S3Fidelity.ErrorPair(() => S3Fidelity.CompleteInListedOrder(ctx, bucket, key, uploadId, [S3Fidelity.Part(2, p2.ETag), S3Fidelity.Part(1, p1.ETag)]));
            Ensure(code == "InvalidPartOrder", $"expected InvalidPartOrder, got {Py.Repr(code)}");
            Ensure(message == "The list of parts was not in ascending order. Parts must be ordered by part number.", message ?? "None");

            // Effect-absent, via an independent read: the rejected attempt must not have consumed
            // or dropped the uploaded parts.
            var still = (await s3.ListPartsAsync(new ListPartsRequest { BucketName = bucket, Key = key, UploadId = uploadId })).Parts ?? [];
            var stillByNumber = still.ToDictionary(p => p.PartNumber ?? 0, p => p.ETag);
            Ensure(
                stillByNumber.Count == 2 && stillByNumber.GetValueOrDefault(1) == p1.ETag && stillByNumber.GetValueOrDefault(2) == p2.ETag,
                $"rejected out-of-order completion must not alter the uploaded parts, got {Py.Repr(stillByNumber)}");

            // Restore: the identical upload, with the Parts list corrected to ascending order,
            // must now succeed -- proving the rejection was about order, not a blanket refusal.
            var result = await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = bucket,
                Key = key,
                UploadId = uploadId,
                PartETags = [S3Fidelity.Part(1, p1.ETag), S3Fidelity.Part(2, p2.ETag)],
            });
            completed = true;
            Ensure(result.Key == key, $"unexpected CompleteMultipartUpload response: Key={Py.Repr(result.Key)}");

            // Terminal assertion: an independent read at the destination, through a different
            // operation than the one that mutated.
            var body = await S3Fidelity.GetBytes(s3, bucket, key);
            Ensure(body.AsSpan().SequenceEqual([.. part1Body, .. part2Body]), "assembled object must equal part1+part2 in order");
        }
        finally
        {
            if (!completed)
            {
                await s3.AbortMultipartUploadAsync(bucket, key, uploadId);
            }
        }
    }
}

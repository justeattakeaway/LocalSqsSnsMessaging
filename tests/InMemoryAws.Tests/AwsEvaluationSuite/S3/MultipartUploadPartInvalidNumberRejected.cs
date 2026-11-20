// Translated from behavioral/s3/check_multipart_upload_part_invalid_number_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.multipart-upload-part-invalid-number-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class MultipartUploadPartInvalidNumberRejected
{
    private const string Check = "multipart-upload-part-invalid-number-rejected";

    /// <summary>
    /// UploadPart with PartNumber outside 1..10000 must be rejected with InvalidArgument, while a
    /// valid PartNumber on the same upload still succeeds.
    /// </summary>
    [Test]
    [DisplayName("s3.multipart-upload-part-invalid-number-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateTaggedBucket(ctx, "simparity-check", Check);
        const string key = "mpu-badpartnum.bin";
        var uploadId = (await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = key })).UploadId;

        // Positive control: a valid PartNumber must succeed on this same upload, so the negatives
        // below prove a specific rejection rather than a target that refuses UploadPart outright.
        var good = await s3.UploadPartAsync(new UploadPartRequest { BucketName = bucket, Key = key, PartNumber = 1, UploadId = uploadId, InputStream = new MemoryStream("good"u8.ToArray()) });
        Ensure(!string.IsNullOrEmpty(good.ETag), $"valid PartNumber=1 must succeed with an ETag, got {Py.Repr(good.ETag)}");

        foreach (var badPartNumber in new[] { 0, 10001 })
        {
            var (code, message) = await S3Fidelity.ErrorPair(
                () => s3.UploadPartAsync(new UploadPartRequest { BucketName = bucket, Key = key, PartNumber = badPartNumber, UploadId = uploadId, InputStream = new MemoryStream("x"u8.ToArray()) }),
                "expected rejection, but the call SUCCEEDED");
            Ensure(code == "InvalidArgument", $"expected InvalidArgument for PartNumber={badPartNumber}, got {Py.Repr(code)}");
            Ensure(message == "Part number must be an integer between 1 and 10000, inclusive", message ?? "None");
        }

        var parts = (await s3.ListPartsAsync(new ListPartsRequest { BucketName = bucket, Key = key, UploadId = uploadId })).Parts ?? [];
        var partNumbers = parts.Select(p => p.PartNumber).Order().ToList();
        Ensure(
            partNumbers.SequenceEqual([1]),
            $"only the valid PartNumber=1 part must be recorded, rejected parts must leave no trace, got {Py.Repr(partNumbers)}");

        // Cleanup
        await s3.AbortMultipartUploadAsync(bucket, key, uploadId);
    }
}

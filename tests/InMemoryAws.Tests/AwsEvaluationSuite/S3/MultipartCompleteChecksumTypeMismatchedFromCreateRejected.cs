// Translated from behavioral/s3/check_multipart_complete_checksum_type_mismatched_from_create_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.multipart-complete-checksum-type-mismatched-from-create-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class MultipartCompleteChecksumTypeMismatchedFromCreateRejected
{
    private static readonly byte[] Payload = S3Fidelity.RepeatText("full-object-vs-composite-checksum-type-mismatch-body-", 2048);

    private static readonly (string? Code, string? Message) Mismatch = (
        "InvalidRequest",
        "The upload was created using the FULL_OBJECT checksum mode. The complete request must use the same checksum mode.");

    /// <summary>
    /// CompleteMultipartUpload with ChecksumType COMPOSITE on an upload created with CRC32 +
    /// FULL_OBJECT is rejected, and completing it with the matching FULL_OBJECT type still succeeds.
    /// </summary>
    [Test]
    [DisplayName("s3.multipart-complete-checksum-type-mismatched-from-create-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-mpuctm-");
        const string key = "mismatched-checksum-type.bin";

        var created = await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = bucket,
            Key = key,
            ChecksumAlgorithm = ChecksumAlgorithm.CRC32,
            ChecksumType = ChecksumType.FULL_OBJECT,
        });
        var uploadId = created.UploadId;
        ctx.OnCleanup(S3Fidelity.AbortQuietly(s3, bucket, key, uploadId));

        var partCrc = S3Fidelity.Crc32B64(Payload);
        var uploaded = await s3.UploadPartAsync(new UploadPartRequest
        {
            BucketName = bucket,
            Key = key,
            UploadId = uploadId,
            PartNumber = 1,
            InputStream = new MemoryStream(Payload),
            ChecksumAlgorithm = ChecksumAlgorithm.CRC32,
            ChecksumCRC32 = partCrc,
        });
        List<PartETag> Manifest() => [new PartETag { ETag = uploaded.ETag, PartNumber = 1, ChecksumCRC32 = uploaded.ChecksumCRC32 }];

        var got = await S3Fidelity.ErrorPair(
            () => s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = bucket,
                Key = key,
                UploadId = uploadId,
                PartETags = Manifest(),
                ChecksumType = ChecksumType.COMPOSITE,
            }),
            "expected the call to be rejected, but it succeeded");
        Ensure(
            got == Mismatch,
            $"completing with ChecksumType COMPOSITE against a FULL_OBJECT upload gave {S3Fidelity.Repr(got)}; want {S3Fidelity.Repr(Mismatch)}");

        // Effect absent: the refused completion left no object.
        var listed = await S3CoreFidelity.ListAllKeys(s3, bucket);
        Ensure(listed.Count == 0, $"the rejected CompleteMultipartUpload left an object behind: {Py.Repr(listed)}");

        // Positive control: the same upload, the same parts, completed with the matching type.
        var wantWhole = S3Fidelity.Crc32B64(Payload);
        var completed = await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket,
            Key = key,
            UploadId = uploadId,
            PartETags = Manifest(),
            ChecksumType = ChecksumType.FULL_OBJECT,
            ChecksumCRC32 = wantWhole,
            MpuObjectSize = Payload.Length,
        });
        Ensure(completed.ChecksumType?.Value == "FULL_OBJECT", $"the matching completion reported ChecksumType {Py.Repr(completed.ChecksumType?.Value)}");
        Ensure(completed.ChecksumCRC32 == wantWhole, $"the matching completion reported ChecksumCRC32 {Py.Repr(completed.ChecksumCRC32)}; want {Py.Repr(wantWhole)}");

        var head = await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = key, ChecksumMode = ChecksumMode.ENABLED });
        Ensure(
            head.ChecksumType?.Value == "FULL_OBJECT" && head.ChecksumCRC32 == wantWhole,
            $"HeadObject reported ({Py.Repr(head.ChecksumType?.Value)}, {Py.Repr(head.ChecksumCRC32)}); want ('FULL_OBJECT', {Py.Repr(wantWhole)})");
    }
}

// Translated from behavioral/s3/check_multipart_complete_full_object_against_default_composite_upload_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Buffers.Binary;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.multipart-complete-full-object-against-default-composite-upload-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class MultipartCompleteFullObjectAgainstDefaultCompositeUploadRejected
{
    private static readonly byte[] Payload = S3Fidelity.RepeatText("default-checksum-type-vs-full-object-complete-mismatch-body-", 2048);

    private static readonly (string? Code, string? Message) Mismatch = (
        "InvalidRequest",
        "The upload was created using the COMPOSITE checksum mode. The complete request must use the same checksum mode.");

    /// <summary>
    /// CompleteMultipartUpload with ChecksumType FULL_OBJECT on an upload created with CRC32 and
    /// no ChecksumType (COMPOSITE by default) is rejected, and completing it with no ChecksumType
    /// still succeeds.
    /// </summary>
    [Test]
    [DisplayName("s3.multipart-complete-full-object-against-default-composite-upload-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-mpuctd-");
        const string key = "default-checksum-type-vs-full-object.bin";

        var created = await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = bucket,
            Key = key,
            ChecksumAlgorithm = ChecksumAlgorithm.CRC32,
        });
        var uploadId = created.UploadId;
        ctx.OnCleanup(S3Fidelity.AbortQuietly(s3, bucket, key, uploadId));
        Ensure(
            created.ChecksumType?.Value == "COMPOSITE",
            $"CreateMultipartUpload with no ChecksumType echoed {Py.Repr(created.ChecksumType?.Value)}; want 'COMPOSITE'");

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

        var wantWhole = S3Fidelity.Crc32B64(Payload);
        var got = await S3Fidelity.ErrorPair(
            () => s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = bucket,
                Key = key,
                UploadId = uploadId,
                PartETags = Manifest(),
                ChecksumType = ChecksumType.FULL_OBJECT,
                ChecksumCRC32 = wantWhole,
                MpuObjectSize = Payload.Length,
            }),
            "expected the call to be rejected, but it succeeded");
        Ensure(
            got == Mismatch,
            $"completing with ChecksumType FULL_OBJECT against a default-COMPOSITE upload gave {S3Fidelity.Repr(got)}; want {S3Fidelity.Repr(Mismatch)}");

        // Effect absent: the refused completion left no object.
        var listed = await S3CoreFidelity.ListAllKeys(s3, bucket);
        Ensure(listed.Count == 0, $"the rejected CompleteMultipartUpload left an object behind: {Py.Repr(listed)}");

        // Positive control: the same upload, the same part, completed with no ChecksumType.
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, S3Fidelity.Crc32(Payload));
        var wantComposite = S3Fidelity.Crc32B64(crc) + "-1";
        var completed = await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket,
            Key = key,
            UploadId = uploadId,
            PartETags = Manifest(),
        });
        Ensure(completed.ChecksumType?.Value == "COMPOSITE", $"the matching completion reported ChecksumType {Py.Repr(completed.ChecksumType?.Value)}");
        Ensure(
            completed.ChecksumCRC32 == wantComposite,
            $"the matching completion reported ChecksumCRC32 {Py.Repr(completed.ChecksumCRC32)}; want {Py.Repr(wantComposite)}");

        var head = await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = key, ChecksumMode = ChecksumMode.ENABLED });
        Ensure(
            head.ChecksumType?.Value == "COMPOSITE" && head.ChecksumCRC32 == wantComposite,
            $"HeadObject reported ({Py.Repr(head.ChecksumType?.Value)}, {Py.Repr(head.ChecksumCRC32)}); want ('COMPOSITE', {Py.Repr(wantComposite)})");
    }
}

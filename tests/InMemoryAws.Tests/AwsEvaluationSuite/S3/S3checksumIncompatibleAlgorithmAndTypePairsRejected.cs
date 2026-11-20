// Translated from behavioral/s3/check_s3checksum_incompatible_algorithm_and_type_pairs_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Net;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3checksum-incompatible-algorithm-and-type-pairs-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3checksumIncompatibleAlgorithmAndTypePairsRejected
{
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("one-part-multipart-body-whose-full-object-crc32c-is-checked-", 64)));

    /// <summary>
    /// SHA256+FULL_OBJECT and CRC64NVME+COMPOSITE multipart uploads are refused with InvalidRequest.
    /// Neither refusal opens an upload; SHA256+COMPOSITE is accepted, and CRC32C+FULL_OBJECT completes
    /// with a whole-object CRC32C matching a locally computed Castagnoli digest.
    /// </summary>
    [Test]
    [DisplayName("s3.s3checksum-incompatible-algorithm-and-type-pairs-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");

        Task<InitiateMultipartUploadResponse> Create(string key, ChecksumAlgorithm algorithm, ChecksumType type) =>
            s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = key, ChecksumAlgorithm = algorithm, ChecksumType = type });

        // --- Refusal 1: SHA256 has no whole-object form.
        var shaFull = await FidelityContext.CaptureError(() => Create("sha256-full-object", ChecksumAlgorithm.SHA256, ChecksumType.FULL_OBJECT));
        Ensure(
            shaFull is not null,
            "CreateMultipartUpload accepted ChecksumAlgorithm=SHA256 with ChecksumType=FULL_OBJECT; SHA-256 cannot be combined across parts, so there is no whole-object form to produce");
        Ensure(shaFull!.Code == "InvalidRequest", $"expected InvalidRequest for SHA256 + FULL_OBJECT, got {Py.Repr(shaFull.Code)}");
        Ensure(shaFull.StatusCode == HttpStatusCode.BadRequest, $"expected HTTP 400, got {(int)shaFull.StatusCode}");
        Ensure(
            shaFull.Message == "The FULL_OBJECT checksum type cannot be used with the sha256 checksum algorithm.",
            $"the SHA256 + FULL_OBJECT refusal's message is {Py.Repr(shaFull.Message)}");

        // --- Refusal 2: CRC64NVME has no composite form.
        var crc64Composite = await FidelityContext.CaptureError(() => Create("crc64nvme-composite", ChecksumAlgorithm.CRC64NVME, ChecksumType.COMPOSITE));
        Ensure(
            crc64Composite is not null,
            "CreateMultipartUpload accepted ChecksumAlgorithm=CRC64NVME with ChecksumType=COMPOSITE; CRC64NVME is composable, so AWS offers only its whole-object form");
        Ensure(crc64Composite!.Code == "InvalidRequest", $"expected InvalidRequest for CRC64NVME + COMPOSITE, got {Py.Repr(crc64Composite.Code)}");
        Ensure(crc64Composite.StatusCode == HttpStatusCode.BadRequest, $"expected HTTP 400, got {(int)crc64Composite.StatusCode}");
        Ensure(
            crc64Composite.Message == "The COMPOSITE checksum type cannot be used with the crc64nvme checksum algorithm.",
            $"the CRC64NVME + COMPOSITE refusal's message is {Py.Repr(crc64Composite.Message)}");

        // --- Effect absent: neither refusal created an upload to leak or complete.
        var openUploads = ((await s3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = bucket })).MultipartUploads ?? []).Select(u => u.Key).ToList();
        Ensure(openUploads.Count == 0, $"the two refused CreateMultipartUpload calls left {Py.Repr(openUploads)} open; a refusal must not mint an upload id");

        // --- POSITIVE CONTROL A: the legal composite pair for the same algorithm that was refused.
        var legalSha = await Create("sha256-composite", ChecksumAlgorithm.SHA256, ChecksumType.COMPOSITE);
        ctx.OnCleanup(S3Fidelity.AbortQuietly(s3, bucket, "sha256-composite", legalSha.UploadId));
        Ensure(legalSha.ChecksumType == ChecksumType.COMPOSITE, $"the legal SHA256 pair reported ChecksumType {Py.Repr(legalSha.ChecksumType?.Value)}");
        Ensure(legalSha.ChecksumAlgorithm == ChecksumAlgorithm.SHA256, $"the legal SHA256 pair reported ChecksumAlgorithm {Py.Repr(legalSha.ChecksumAlgorithm?.Value)}");

        // --- POSITIVE CONTROL B: the legal whole-object pair, carried through to a real object.
        var want = S3Fidelity.Crc32CB64(Payload);
        var legalCrc = await Create("crc32c-full-object", ChecksumAlgorithm.CRC32C, ChecksumType.FULL_OBJECT);
        var crcUploadId = legalCrc.UploadId;
        ctx.OnCleanup(S3Fidelity.AbortQuietly(s3, bucket, "crc32c-full-object", crcUploadId));
        Ensure(legalCrc.ChecksumType == ChecksumType.FULL_OBJECT, $"the legal CRC32C pair reported ChecksumType {Py.Repr(legalCrc.ChecksumType?.Value)}");
        var uploaded = await s3.UploadPartAsync(new UploadPartRequest
        {
            BucketName = bucket,
            Key = "crc32c-full-object",
            UploadId = crcUploadId,
            PartNumber = 1,
            InputStream = new MemoryStream(Payload),
            ChecksumAlgorithm = ChecksumAlgorithm.CRC32C,
            ChecksumCRC32C = want,
        });
        Ensure(
            uploaded.ChecksumCRC32C == want,
            $"UploadPart reported {Py.Repr(uploaded.ChecksumCRC32C)}, the client computed {Py.Repr(want)} from the Castagnoli polynomial over the same {Payload.Length} bytes");
        var done = await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket,
            Key = "crc32c-full-object",
            UploadId = crcUploadId,
            PartETags = [new PartETag { PartNumber = 1, ETag = uploaded.ETag, ChecksumCRC32C = uploaded.ChecksumCRC32C }],
            ChecksumType = ChecksumType.FULL_OBJECT,
            ChecksumCRC32C = want,
            MpuObjectSize = Payload.Length,
        });
        Ensure(done.ChecksumType == ChecksumType.FULL_OBJECT, $"the completion reported ChecksumType {Py.Repr(done.ChecksumType?.Value)}, want 'FULL_OBJECT'");
        Ensure(done.ChecksumCRC32C == want, $"the completion reported {Py.Repr(done.ChecksumCRC32C)}; a FULL_OBJECT CRC32C over these bytes is {Py.Repr(want)}");
        Ensure(!done.ChecksumCRC32C.Contains('-', StringComparison.Ordinal), $"a FULL_OBJECT checksum carries no part-count suffix; got {Py.Repr(done.ChecksumCRC32C)}");
        using (var got = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = "crc32c-full-object", ChecksumMode = ChecksumMode.ENABLED }))
        {
            var body = await S3Fidelity.ReadBody(got);
            Ensure(body.AsSpan().SequenceEqual(Payload), $"the completed object is {body.Length} bytes, want {Payload.Length}");
            Ensure(
                got.ChecksumCRC32C == want && want == S3Fidelity.Crc32CB64(body),
                $"GetObject reported {Py.Repr(got.ChecksumCRC32C)}; the bytes it returned checksum to {Py.Repr(S3Fidelity.Crc32CB64(body))} and the client computed {Py.Repr(want)} before uploading them");
            Ensure(got.ChecksumType == ChecksumType.FULL_OBJECT, $"GetObject reported ChecksumType {Py.Repr(got.ChecksumType?.Value)}, want 'FULL_OBJECT'");
        }

        // Only the legal upload produced an object; the refused pairs produced none.
        var keys = (await S3Fidelity.ListedObjects(s3, bucket)).Select(o => o.Key).ToList();
        Ensure(keys.SequenceEqual(["crc32c-full-object"]), $"the bucket holds {Py.Repr(keys)}; only the completed legal upload may have produced an object");
    }
}

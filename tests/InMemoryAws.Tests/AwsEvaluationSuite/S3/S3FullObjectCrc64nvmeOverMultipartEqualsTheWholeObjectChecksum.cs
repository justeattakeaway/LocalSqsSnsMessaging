// Translated from behavioral/s3/check_s3_full_object_crc64nvme_over_multipart_equals_the_whole_object_checksum.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3-full-object-crc64nvme-over-multipart-equals-the-whole-object-checksum</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3FullObjectCrc64nvmeOverMultipartEqualsTheWholeObjectChecksum
{
    /// <summary>5 MiB — the minimum size for a non-final part.</summary>
    private const int PartSize = 5 * 1024 * 1024;
    private const int NParts = 3;
    private const int Total = PartSize * NParts;

    /// <summary>
    /// A FULL_OBJECT multipart checksum (CRC64NVME and CRC32) equals the checksum of the assembled
    /// bytes, while COMPOSITE over the same parts is the CRC32 of the part digests suffixed "-3".
    /// Reference: https://docs.aws.amazon.com/AmazonS3/latest/userguide/checking-object-integrity.html
    /// </summary>
    [Test]
    [DisplayName("s3.s3-full-object-crc64nvme-over-multipart-equals-the-whole-object-checksum")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");

        var parts = Enumerable.Range(0, NParts).Select(_ => S3Fidelity.RandomBytes(PartSize)).ToList();
        var whole = parts.SelectMany(p => p).ToArray();
        Ensure(whole.Length == Total, $"payload is {whole.Length} bytes, expected {Total}");

        // The check combines the part CRCs into the whole-object CRC64 (to hash each part once);
        // computing it over the assembled bytes gives the same value.
        var wantCrc64 = S3Fidelity.Crc64NvmeB64(whole);
        var wantCrc32 = S3Fidelity.Crc32B64(whole);
        // COMPOSITE is defined as the checksum of the concatenated RAW part digests, then "-N".
        var wantComposite = S3Fidelity.Crc32B64([.. parts.SelectMany(p => Convert.FromBase64String(S3Fidelity.Crc32B64(p)))]) + $"-{NParts}";

        // Upload the same three parts under one (algorithm, ChecksumType) pair.
        async Task<CompleteMultipartUploadResponse> Upload(string key, ChecksumAlgorithm algorithm, ChecksumType checksumType, Action<CompleteMultipartUploadRequest> complete)
        {
            var created = await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = key, ChecksumAlgorithm = algorithm, ChecksumType = checksumType });
            var uploadId = created.UploadId;
            // Registered before any assertion: an open upload keeps billing for its parts.
            ctx.OnCleanup(S3Fidelity.AbortQuietly(s3, bucket, key, uploadId));
            Ensure(created.ChecksumType == checksumType, $"CreateMultipartUpload echoed ChecksumType {Py.Repr(created.ChecksumType?.Value)}, asked for {Py.Repr(checksumType.Value)}");
            var crc64 = algorithm == ChecksumAlgorithm.CRC64NVME;
            var manifest = new List<PartETag>();
            for (var number = 1; number <= parts.Count; number++)
            {
                var chunk = parts[number - 1];
                // Supplied explicitly: botocore computes CRC64NVME only with awscrt installed.
                // S3 still verifies the bytes against the value sent.
                var partChecksum = crc64 ? S3Fidelity.Crc64NvmeB64(chunk) : S3Fidelity.Crc32B64(chunk);
                var request = new UploadPartRequest { BucketName = bucket, Key = key, UploadId = uploadId, PartNumber = number, InputStream = new MemoryStream(chunk), ChecksumAlgorithm = algorithm };
                if (crc64)
                {
                    request.ChecksumCRC64NVME = partChecksum;
                }
                else
                {
                    request.ChecksumCRC32 = partChecksum;
                }

                var uploaded = await s3.UploadPartAsync(request);
                var echoed = crc64 ? uploaded.ChecksumCRC64NVME : uploaded.ChecksumCRC32;
                Ensure(echoed == partChecksum, $"UploadPart echoed {(crc64 ? "ChecksumCRC64NVME" : "ChecksumCRC32")} {Py.Repr(echoed)} for part {number}; the client sent {Py.Repr(partChecksum)}");
                manifest.Add(crc64
                    ? new PartETag { ETag = uploaded.ETag, PartNumber = number, ChecksumCRC64NVME = echoed }
                    : new PartETag { ETag = uploaded.ETag, PartNumber = number, ChecksumCRC32 = echoed });
            }

            var completeRequest = new CompleteMultipartUploadRequest { BucketName = bucket, Key = key, UploadId = uploadId, PartETags = manifest };
            complete(completeRequest);
            return await s3.CompleteMultipartUploadAsync(completeRequest);
        }

        // Leg 1: FULL_OBJECT / CRC64NVME.
        var full64 = await Upload("mpu-fullobject-crc64.bin", ChecksumAlgorithm.CRC64NVME, ChecksumType.FULL_OBJECT, r =>
        {
            r.ChecksumType = ChecksumType.FULL_OBJECT;
            r.ChecksumCRC64NVME = wantCrc64;
            r.MpuObjectSize = Total;
        });
        Ensure(full64.ChecksumType == ChecksumType.FULL_OBJECT, $"a FULL_OBJECT completion reported ChecksumType {Py.Repr(full64.ChecksumType?.Value)}");
        Ensure(
            !full64.ChecksumCRC64NVME.Contains('-', StringComparison.Ordinal),
            $"a FULL_OBJECT checksum carried a part suffix: {Py.Repr(full64.ChecksumCRC64NVME)} — that is the COMPOSITE form, so the whole-object mode was not actually honoured");
        Ensure(full64.ChecksumCRC64NVME == wantCrc64, $"S3 returned CRC64NVME {Py.Repr(full64.ChecksumCRC64NVME)} for the assembled object; the client computed {Py.Repr(wantCrc64)} over the same {Total} bytes");

        // The stored checksum must survive a read, not just be echoed by the write.
        using (var got64 = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = "mpu-fullobject-crc64.bin", ChecksumMode = ChecksumMode.ENABLED }))
        {
            Ensure(got64.ChecksumCRC64NVME == wantCrc64, $"GetObject reported CRC64NVME {Py.Repr(got64.ChecksumCRC64NVME)}, want {Py.Repr(wantCrc64)}");
            Ensure(got64.ChecksumType == ChecksumType.FULL_OBJECT, $"GetObject reported ChecksumType {Py.Repr(got64.ChecksumType?.Value)}, want 'FULL_OBJECT'");
            Ensure((await S3Fidelity.ReadBody(got64)).AsSpan().SequenceEqual(whole), "the assembled object's bytes are not the three parts");
        }

        // HeadObject without PartNumber returns no PartsCount, so the count is read here.
        var attrs64 = await s3.GetObjectAttributesAsync(new GetObjectAttributesRequest
        {
            BucketName = bucket,
            Key = "mpu-fullobject-crc64.bin",
            ObjectAttributes = [ObjectAttributes.Checksum, ObjectAttributes.ObjectParts, ObjectAttributes.ObjectSize],
        });
        Ensure(
            attrs64.ObjectParts?.TotalPartsCount == NParts,
            $"GetObjectAttributes reported {Py.Repr(attrs64.ObjectParts?.TotalPartsCount)} parts, want {NParts} — the part boundaries were flattened by the completion");
        Ensure(attrs64.ObjectSize == Total, $"GetObjectAttributes reported ObjectSize {Py.Repr(attrs64.ObjectSize)}, want {Total}");

        // Leg 2: FULL_OBJECT / CRC32. CRC64NVME cannot be COMPOSITE (CreateMultipartUpload fails
        // InvalidRequest), so the two modes are compared under CRC32.
        var full32 = await Upload("mpu-fullobject-crc32.bin", ChecksumAlgorithm.CRC32, ChecksumType.FULL_OBJECT, r =>
        {
            r.ChecksumType = ChecksumType.FULL_OBJECT;
            r.ChecksumCRC32 = wantCrc32;
            r.MpuObjectSize = Total;
        });
        Ensure(full32.ChecksumType == ChecksumType.FULL_OBJECT, $"a FULL_OBJECT completion reported ChecksumType {Py.Repr(full32.ChecksumType?.Value)}");
        Ensure(full32.ChecksumCRC32 == wantCrc32, $"S3 returned CRC32 {Py.Repr(full32.ChecksumCRC32)} for the assembled object; the client computed {Py.Repr(wantCrc32)} over the same bytes");
        Ensure(!full32.ChecksumCRC32.Contains('-', StringComparison.Ordinal), $"a FULL_OBJECT CRC32 carried a part suffix: {Py.Repr(full32.ChecksumCRC32)}");

        // Leg 3: COMPOSITE / CRC32 over the identical parts.
        var composite = await Upload("mpu-composite-crc32.bin", ChecksumAlgorithm.CRC32, ChecksumType.COMPOSITE, _ => { });
        Ensure(composite.ChecksumType == ChecksumType.COMPOSITE, $"a COMPOSITE completion reported ChecksumType {Py.Repr(composite.ChecksumType?.Value)}");
        Ensure(
            composite.ChecksumCRC32 == wantComposite,
            $"S3 returned COMPOSITE CRC32 {Py.Repr(composite.ChecksumCRC32)}; the client computed {Py.Repr(wantComposite)} as crc32(concat of the three raw part digests) + '-{NParts}'");
        Ensure(
            composite.ChecksumCRC32 != wantCrc32,
            $"the COMPOSITE checksum equalled the whole-object one ({Py.Repr(wantCrc32)}) — the two modes are not distinguished, so ChecksumType is being ignored");

        // GetObject carries the "-N" suffix; GetObjectAttributes carries the bare digest.
        using (var gotComp = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = "mpu-composite-crc32.bin", ChecksumMode = ChecksumMode.ENABLED }))
        {
            Ensure(gotComp.ChecksumCRC32 == wantComposite, $"GetObject reported COMPOSITE CRC32 {Py.Repr(gotComp.ChecksumCRC32)}, want {Py.Repr(wantComposite)}");
            Ensure(
                (await S3Fidelity.ReadBody(gotComp)).AsSpan().SequenceEqual(whole),
                "the COMPOSITE object's bytes are not the three parts — the checksum mode changed the data");
        }

        var attrsComp = await s3.GetObjectAttributesAsync(new GetObjectAttributesRequest
        {
            BucketName = bucket,
            Key = "mpu-composite-crc32.bin",
            ObjectAttributes = [ObjectAttributes.Checksum, ObjectAttributes.ObjectParts],
        });
        var bare = wantComposite[..wantComposite.LastIndexOf('-')];
        Ensure(
            attrsComp.Checksum?.ChecksumCRC32 == bare,
            $"GetObjectAttributes reported {Py.Repr(attrsComp.Checksum?.ChecksumCRC32)}; it returns the COMPOSITE digest WITHOUT the '-{NParts}' suffix, want {Py.Repr(bare)}");
        var perPart = (attrsComp.ObjectParts?.Parts ?? []).ToDictionary(p => p.PartNumber ?? 0, p => p.ChecksumCRC32);
        var expectedPerPart = Enumerable.Range(1, NParts).ToDictionary(i => i, i => S3Fidelity.Crc32B64(parts[i - 1]));
        Ensure(
            perPart.Count == expectedPerPart.Count && expectedPerPart.All(e => perPart.GetValueOrDefault(e.Key) == e.Value),
            $"the per-part CRC32s S3 stored are {Py.Repr(perPart)}; the client computed {Py.Repr(expectedPerPart)}");

        foreach (var (label, etag) in new[] { ("FULL_OBJECT/CRC64NVME", full64.ETag), ("FULL_OBJECT/CRC32", full32.ETag), ("COMPOSITE/CRC32", composite.ETag) })
        {
            Ensure(
                etag.Trim('"').EndsWith($"-{NParts}", StringComparison.Ordinal),
                $"{label} produced ETag {Py.Repr(etag)}, which is not part-count suffixed — a completed multipart object's ETag is never a plain MD5");
        }
    }
}

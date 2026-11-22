// Translated from behavioral/s3/check_crc32_multipart_default_composite_vs_full_object_checksum.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Buffers.Binary;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.crc32-multipart-default-composite-vs-full-object-checksum</summary>
[Category("AwsEvaluationSuite")]
public sealed class Crc32MultipartDefaultCompositeVsFullObjectChecksum
{
    /// <summary>The multipart minimum for a non-final part.</summary>
    private const int Part1Len = 5 * 1024 * 1024;
    private const int Part2Len = 1024 * 1024;

    /// <summary>
    /// A CRC32 multipart upload with no ChecksumType defaults to COMPOSITE, whose ETag and
    /// ChecksumCRC32 are built from the parts' own digests; FULL_OBJECT over identical bytes
    /// instead reports the plain CRC32 of the assembled object, under the same part-suffixed ETag.
    /// </summary>
    [Test]
    [DisplayName("s3.crc32-multipart-default-composite-vs-full-object-checksum")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-crcty-");

        var part1 = new byte[Part1Len];
        for (var i = 0; i < Part1Len; i++)
        {
            part1[i] = (byte)(((i * 5) + 2) % 251);
        }

        var part2 = new byte[Part2Len];
        for (var i = 0; i < Part2Len; i++)
        {
            part2[i] = (byte)(((i * 13) + 7) % 251);
        }

        byte[] whole = [.. part1, .. part2];

        string[] partCrcs = [S3Fidelity.Crc32B64(part1), S3Fidelity.Crc32B64(part2)];
        var crcBytes = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, S3Fidelity.Crc32(part1));
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes.AsSpan(4), S3Fidelity.Crc32(part2));
        var wantCompositeCrc32 = S3Fidelity.Crc32B64(crcBytes) + "-2";
        var wantFullObjectCrc32 = S3Fidelity.Crc32B64(whole);
        var wantEtag = S3Fidelity.MultipartETag(part1, part2);

        async Task<(InitiateMultipartUploadResponse Created, CompleteMultipartUploadResponse Completed)> Upload(
            string key, ChecksumType? createType, Action<CompleteMultipartUploadRequest> complete)
        {
            var created = await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
            {
                BucketName = bucket,
                Key = key,
                ChecksumAlgorithm = ChecksumAlgorithm.CRC32,
                ChecksumType = createType,
            });
            var uploadId = created.UploadId;
            ctx.OnCleanup(S3Fidelity.AbortQuietly(s3, bucket, key, uploadId));
            var manifest = new List<PartETag>();
            foreach (var (number, chunk, crc) in new[] { (1, part1, partCrcs[0]), (2, part2, partCrcs[1]) })
            {
                var uploaded = await s3.UploadPartAsync(new UploadPartRequest
                {
                    BucketName = bucket,
                    Key = key,
                    UploadId = uploadId,
                    PartNumber = number,
                    InputStream = new MemoryStream(chunk),
                    ChecksumAlgorithm = ChecksumAlgorithm.CRC32,
                    ChecksumCRC32 = crc,
                });
                manifest.Add(new PartETag { ETag = uploaded.ETag, PartNumber = number, ChecksumCRC32 = uploaded.ChecksumCRC32 });
            }

            var request = new CompleteMultipartUploadRequest { BucketName = bucket, Key = key, UploadId = uploadId, PartETags = manifest };
            complete(request);
            return (created, await s3.CompleteMultipartUploadAsync(request));
        }

        // Leg 1: no ChecksumType anywhere — the SDK's own CRC32 default is COMPOSITE.
        var (createdA, completedA) = await Upload("default-checksum-type.bin", null, _ => { });
        Ensure(
            createdA.ChecksumType?.Value == "COMPOSITE",
            $"CreateMultipartUpload with ChecksumAlgorithm CRC32 and no ChecksumType echoed ChecksumType {Py.Repr(createdA.ChecksumType?.Value)}; want 'COMPOSITE'");
        Ensure(
            completedA.ETag == wantEtag,
            $"the default-ChecksumType completion returned ETag {Py.Repr(completedA.ETag)}; want {Py.Repr(wantEtag)} (md5 of the two parts' own md5 digests, suffixed '-2')");
        Ensure(
            completedA.ChecksumCRC32 == wantCompositeCrc32,
            $"the default-ChecksumType completion returned ChecksumCRC32 {Py.Repr(completedA.ChecksumCRC32)}; want {Py.Repr(wantCompositeCrc32)} (crc32 of the two parts' own crc32 digests, suffixed '-2')");

        var headA = await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = "default-checksum-type.bin", ChecksumMode = ChecksumMode.ENABLED });
        Ensure(
            headA.ChecksumType?.Value == "COMPOSITE",
            $"HeadObject on the default-ChecksumType object reported ChecksumType {Py.Repr(headA.ChecksumType?.Value)}; want 'COMPOSITE'");
        Ensure(headA.ETag == wantEtag, $"HeadObject on the default-ChecksumType object reported ETag {Py.Repr(headA.ETag)}; want {Py.Repr(wantEtag)}");
        Ensure(
            headA.ChecksumCRC32 == wantCompositeCrc32,
            $"HeadObject on the default-ChecksumType object reported ChecksumCRC32 {Py.Repr(headA.ChecksumCRC32)}; want {Py.Repr(wantCompositeCrc32)}");

        var attrsA = await s3.GetObjectAttributesAsync(new GetObjectAttributesRequest
        {
            BucketName = bucket,
            Key = "default-checksum-type.bin",
            ObjectAttributes = [ObjectAttributes.ObjectParts],
        });
        var perPartA = (attrsA.ObjectParts?.Parts ?? []).ToDictionary(p => (object)p.PartNumber!, p => (object?)p.ChecksumCRC32);
        Ensure(
            Py.Equal(perPartA, new Dictionary<object, object?> { [1] = partCrcs[0], [2] = partCrcs[1] }),
            $"GetObjectAttributes ObjectParts carried per-part CRC32s {Py.Repr(perPartA)}; want {{1: {Py.Repr(partCrcs[0])}, 2: {Py.Repr(partCrcs[1])}}}, the digests of the uploaded bytes");

        // Leg 2: identical bytes, ChecksumType pinned to FULL_OBJECT throughout.
        var (_, completedB) = await Upload("full-object-checksum-type.bin", ChecksumType.FULL_OBJECT, request =>
        {
            request.ChecksumType = ChecksumType.FULL_OBJECT;
            request.ChecksumCRC32 = wantFullObjectCrc32;
            request.MpuObjectSize = whole.Length;
        });
        Ensure(
            completedB.ChecksumType?.Value == "FULL_OBJECT",
            $"the FULL_OBJECT completion reported ChecksumType {Py.Repr(completedB.ChecksumType?.Value)}; want 'FULL_OBJECT'");
        Ensure(
            completedB.ChecksumCRC32 == wantFullObjectCrc32,
            $"the FULL_OBJECT completion returned ChecksumCRC32 {Py.Repr(completedB.ChecksumCRC32)}; want {Py.Repr(wantFullObjectCrc32)} (the plain crc32 of the assembled bytes)");
        Ensure(
            completedB.ETag == wantEtag,
            $"the FULL_OBJECT completion returned ETag {Py.Repr(completedB.ETag)}; want the same {Py.Repr(wantEtag)} as the COMPOSITE completion of the identical parts — ETag does not depend on ChecksumType");

        var headB = await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = "full-object-checksum-type.bin", ChecksumMode = ChecksumMode.ENABLED });
        Ensure(
            headB.ChecksumType?.Value == "FULL_OBJECT",
            $"HeadObject on the FULL_OBJECT object reported ChecksumType {Py.Repr(headB.ChecksumType?.Value)}; want 'FULL_OBJECT'");
        Ensure(
            headB.ChecksumCRC32 == wantFullObjectCrc32,
            $"HeadObject on the FULL_OBJECT object reported ChecksumCRC32 {Py.Repr(headB.ChecksumCRC32)}; want {Py.Repr(wantFullObjectCrc32)}, with no part suffix");
        Ensure(headB.ETag == wantEtag, $"HeadObject on the FULL_OBJECT object reported ETag {Py.Repr(headB.ETag)}; want {Py.Repr(wantEtag)}");

        Ensure(
            headA.ChecksumCRC32 != headB.ChecksumCRC32,
            $"the COMPOSITE and FULL_OBJECT checksums of the identical bytes were both {Py.Repr(headA.ChecksumCRC32)} — the two modes are not actually distinguished");
    }
}

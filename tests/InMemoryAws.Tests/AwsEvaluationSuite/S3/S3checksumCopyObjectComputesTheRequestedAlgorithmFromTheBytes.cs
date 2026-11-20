// Translated from behavioral/s3/check_s3checksum_copy_object_computes_the_requested_algorithm_from_the_bytes.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3checksum-copy-object-computes-the-requested-algorithm-from-the-bytes</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3checksumCopyObjectComputesTheRequestedAlgorithmFromTheBytes
{
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("the-copy-must-be-hashed-server-side-with-an-algorithm-the-source-never-had-", 48)));

    /// <summary>
    /// CopyObject with ChecksumAlgorithm=SHA256 from a source holding only a CRC32C returns the SHA-256
    /// of the bytes and stores it on the destination, a copy with no algorithm carries the source's CRC32C,
    /// and the source is unchanged.
    /// </summary>
    [Test]
    [DisplayName("s3.s3checksum-copy-object-computes-the-requested-algorithm-from-the-bytes")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");

        var wantCrc32C = S3Fidelity.Crc32CB64(Payload);
        var wantSha256 = S3Fidelity.Sha256B64(Payload);

        // --- The source: CRC32C only, so no SHA-256 exists anywhere to be forwarded.
        var source = await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "src.bin", InputStream = new MemoryStream(Payload), ChecksumCRC32C = wantCrc32C });
        Ensure(
            source.ChecksumCRC32C == wantCrc32C,
            $"PutObject echoed {Py.Repr(source.ChecksumCRC32C)}, the client computed {Py.Repr(wantCrc32C)} from the Castagnoli polynomial over the same {Payload.Length} bytes");
        var srcHead = await Head(s3, bucket, "src.bin", enabled: true);
        Ensure(
            srcHead.ChecksumSHA256 is null,
            $"the source object already reports ChecksumSHA256={Py.Repr(srcHead.ChecksumSHA256)}; this check requires a source with no SHA-256, so that the copy has nothing to echo");
        Ensure(srcHead.ChecksumCRC32C == wantCrc32C, $"the source reports ChecksumCRC32C={Py.Repr(srcHead.ChecksumCRC32C)}, want {Py.Repr(wantCrc32C)}");

        // --- The copy asks for an algorithm the source never had.
        var copied = await s3.CopyObjectAsync(new CopyObjectRequest
        {
            DestinationBucket = bucket,
            DestinationKey = "dst-sha256.bin",
            SourceBucket = bucket,
            SourceKey = "src.bin",
            ChecksumAlgorithm = ChecksumAlgorithm.SHA256,
        });
        Ensure(
            copied.ChecksumSHA256 == wantSha256,
            $"CopyObject reported ChecksumSHA256={Py.Repr(copied.ChecksumSHA256)}; the client computed {Py.Repr(wantSha256)} with hashlib over the source's {Payload.Length} bytes. The source carries only CRC32C {Py.Repr(wantCrc32C)}, so this value can only come from hashing the data");
        Ensure(
            copied.ChecksumType == ChecksumType.FULL_OBJECT,
            $"a single-part copy's checksum is a whole-object one; got ChecksumType {Py.Repr(copied.ChecksumType?.Value)}");

        // --- Read the destination back, both bytes and digest.
        using (var dst = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = "dst-sha256.bin", ChecksumMode = ChecksumMode.ENABLED }))
        {
            var dstBody = await S3Fidelity.ReadBody(dst);
            Ensure(dstBody.AsSpan().SequenceEqual(Payload), $"the copy is {dstBody.Length} bytes, want {Payload.Length}; a checksum over the wrong bytes is worse than none");
            Ensure(dst.ChecksumSHA256 == wantSha256, $"GetObject on the copy reported {Py.Repr(dst.ChecksumSHA256)}, want {Py.Repr(wantSha256)}");
            Ensure(
                S3Fidelity.Sha256B64(dstBody) == dst.ChecksumSHA256,
                $"the bytes S3 returned hash to {Py.Repr(S3Fidelity.Sha256B64(dstBody))} but S3 reported {Py.Repr(dst.ChecksumSHA256)} for them");
            Ensure(
                dst.ChecksumCRC32C is null,
                $"the copy reports ChecksumCRC32C={Py.Repr(dst.ChecksumCRC32C)} as well; requesting SHA256 replaces the algorithm rather than accumulating the source's");
        }

        var dstAttrs = await s3.GetObjectAttributesAsync(new GetObjectAttributesRequest
        {
            BucketName = bucket,
            Key = "dst-sha256.bin",
            ObjectAttributes = [ObjectAttributes.Checksum, ObjectAttributes.ObjectSize],
        });
        Ensure(dstAttrs.Checksum?.ChecksumSHA256 == wantSha256, $"GetObjectAttributes on the copy reported {Py.Repr(dstAttrs.Checksum?.ChecksumSHA256)}, want {Py.Repr(wantSha256)}");
        Ensure(dstAttrs.ObjectSize == Payload.Length, $"GetObjectAttributes reported ObjectSize {dstAttrs.ObjectSize}, want {Payload.Length}");

        // --- The source is untouched: the copy computed a digest, it did not migrate one.
        var srcAfter = await Head(s3, bucket, "src.bin", enabled: true);
        Ensure(srcAfter.ChecksumCRC32C == wantCrc32C, $"the source now reports ChecksumCRC32C={Py.Repr(srcAfter.ChecksumCRC32C)}, want {Py.Repr(wantCrc32C)}");
        Ensure(srcAfter.ChecksumSHA256 is null, $"the copy gave the SOURCE a ChecksumSHA256={Py.Repr(srcAfter.ChecksumSHA256)}");

        // --- Control on the other branch: a copy that names NO algorithm carries CRC32C forward.
        var plain = await s3.CopyObjectAsync(new CopyObjectRequest { DestinationBucket = bucket, DestinationKey = "dst-default.bin", SourceBucket = bucket, SourceKey = "src.bin" });
        Ensure(
            plain.ChecksumCRC32C == wantCrc32C,
            $"a copy with no ChecksumAlgorithm reported {Py.Repr(plain.ChecksumCRC32C)}; it must preserve the source's CRC32C {Py.Repr(wantCrc32C)}");
        var plainHead = await Head(s3, bucket, "dst-default.bin", enabled: true);
        Ensure(plainHead.ChecksumCRC32C == wantCrc32C, $"the preserved copy reports {Py.Repr(plainHead.ChecksumCRC32C)}, want {Py.Repr(wantCrc32C)}");
        Ensure(
            plainHead.ChecksumSHA256 is null,
            $"the preserved copy invented a ChecksumSHA256={Py.Repr(plainHead.ChecksumSHA256)}; only the copy that ASKED for SHA256 may have one");
        // And the CRC32C really is the Castagnoli digest of the bytes, not zlib's CRC32 of them.
        var zlibCrc32 = S3Fidelity.Crc32B64(Payload);
        Ensure(
            plainHead.ChecksumCRC32C != zlibCrc32,
            $"the preserved CRC32C equals zlib.crc32 of the same bytes ({Py.Repr(zlibCrc32)}); CRC32C uses the Castagnoli polynomial and the two must differ");

        // --- A copy of a key that does not exist must not produce a digest of anything.
        var absent = await FidelityContext.CaptureError(() => s3.CopyObjectAsync(new CopyObjectRequest
        {
            DestinationBucket = bucket,
            DestinationKey = "dst-nothing.bin",
            SourceBucket = bucket,
            SourceKey = "no-such-src.bin",
            ChecksumAlgorithm = ChecksumAlgorithm.SHA256,
        }));
        Ensure(absent is not null, "CopyObject succeeded from a source key that was never created");
        Ensure(absent!.Code == "NoSuchKey", $"expected NoSuchKey copying from a missing source, got {Py.Repr(absent.Code)}");
        Ensure(absent.Message == "The specified key does not exist.", $"the missing-source refusal's message is {Py.Repr(absent.Message)}, want 'The specified key does not exist.'");
        var keys = (await S3Fidelity.ListedObjects(s3, bucket)).Select(o => o.Key).Order(StringComparer.Ordinal).ToList();
        Ensure(keys.SequenceEqual(["dst-default.bin", "dst-sha256.bin", "src.bin"]), $"the bucket holds {Py.Repr(keys)}; the failed copy must have created nothing");
    }

    private static Task<GetObjectMetadataResponse> Head(IAmazonS3 s3, string bucket, string key, bool enabled) =>
        s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = key, ChecksumMode = enabled ? ChecksumMode.ENABLED : null });
}

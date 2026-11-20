// Translated from behavioral/s3/check_cli_s3_cp_large_file_multipart_checksum_defaults.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.cli-s3-cp-large-file-multipart-checksum-defaults</summary>
[Category("AwsEvaluationSuite")]
public sealed class CliS3CpLargeFileMultipartChecksumDefaults
{
    /// <summary>The CLI's default 8 MiB part size splits this into 2 parts.</summary>
    private const int Size = 12 * 1024 * 1024;

    /// <summary>
    /// `aws s3 cp` of a large file uploads in parts; the stored checksum (requested SHA256 or the
    /// CLI's default) is correct over the actual part split, and both uploads download byte-exact.
    /// </summary>
    [Test]
    [DisplayName("s3.cli-s3-cp-large-file-multipart-checksum-defaults")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-clichk-");

        var payload = S3Fidelity.RandomBytes(Size);

        // --- Leg 1: explicit --checksum-algorithm SHA256 (always COMPOSITE for SHA256).
        const string shaKey = "large-sha256.bin";
        // `aws s3 cp large.bin s3://<bucket>/large-sha256.bin --checksum-algorithm SHA256`
        await S3Fidelity.CliUpload(s3, bucket, shaKey, payload, ChecksumAlgorithm.SHA256);

        var shaHead = await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = shaKey, ChecksumMode = ChecksumMode.ENABLED });
        Ensure(
            shaHead.ChecksumType == ChecksumType.COMPOSITE,
            $"a SHA256 upload must be COMPOSITE, got ChecksumType={Py.Repr(shaHead.ChecksumType?.Value)}");
        var shaSizes = await PartSizes(s3, bucket, shaKey);
        Ensure(shaSizes.Count > 1, $"expected a multipart upload of {Size} bytes, got parts {Py.Repr(shaSizes)}");
        Ensure(shaSizes.Sum() == Size, $"part sizes {Py.Repr(shaSizes)} do not sum to {Size}");
        var expectedShaComposite = Sha256CompositeBySizes(payload, shaSizes);
        Ensure(
            shaHead.ChecksumSHA256 == $"{expectedShaComposite}-{shaSizes.Count}",
            $"HeadObject ChecksumSHA256 {Py.Repr(shaHead.ChecksumSHA256)} != the digest computed locally from the real part sizes {Py.Repr(shaSizes)}: {Py.Repr(expectedShaComposite)}-{shaSizes.Count}");

        // `aws s3 cp s3://<bucket>/large-sha256.bin sha256-roundtrip.bin`
        var shaDownload = await S3Fidelity.CliDownload(s3, bucket, shaKey);
        Ensure(shaDownload.AsSpan().SequenceEqual(payload), "the SHA256-checksummed upload did not round-trip byte-exact");

        // No --checksum-algorithm: the default algorithm varies by CLI version, so accept any but
        // require the stored value to be correct for it.
        const string defaultKey = "large-default.bin";
        // `aws s3 cp large.bin s3://<bucket>/large-default.bin`
        await S3Fidelity.CliUpload(s3, bucket, defaultKey, payload);

        var defaultHead = await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = defaultKey, ChecksumMode = ChecksumMode.ENABLED });
        var defaultPartsCount = await TotalParts(s3, bucket, defaultKey);
        Ensure(defaultPartsCount > 1, $"expected the plain cp of {Size} bytes to also be multipart, got {defaultPartsCount} part(s)");
        await AssertDefaultChecksumMatches(defaultHead, payload, s3, bucket, defaultKey);

        // `aws s3 cp s3://<bucket>/large-default.bin default-roundtrip.bin`
        var defaultDownload = await S3Fidelity.CliDownload(s3, bucket, defaultKey);
        Ensure(defaultDownload.AsSpan().SequenceEqual(payload), "the default-checksum upload did not round-trip byte-exact");
    }

    /// <summary>
    /// Per-part sizes. Only reported when ObjectParts actually lists parts — true for a COMPOSITE
    /// checksum (built from per-part digests); a FULL_OBJECT checksum has no per-part state to report
    /// at all, only a TotalPartsCount (see TotalParts).
    /// </summary>
    private static async Task<List<long>> PartSizes(IAmazonS3 s3, string bucket, string key)
    {
        var attrs = await s3.GetObjectAttributesAsync(new GetObjectAttributesRequest { BucketName = bucket, Key = key, ObjectAttributes = [ObjectAttributes.ObjectParts] });
        return [.. (attrs.ObjectParts?.Parts ?? throw Fail("ObjectParts lists no Parts")).Select(p => p.Size ?? 0)];
    }

    private static async Task<int> TotalParts(IAmazonS3 s3, string bucket, string key)
    {
        var attrs = await s3.GetObjectAttributesAsync(new GetObjectAttributesRequest { BucketName = bucket, Key = key, ObjectAttributes = [ObjectAttributes.ObjectParts] });
        return attrs.ObjectParts?.TotalPartsCount ?? 0;
    }

    private static string Sha256CompositeBySizes(byte[] payload, List<long> sizes)
    {
        var offset = 0;
        var digests = new List<byte>();
        foreach (var size in sizes)
        {
            digests.AddRange(SHA256.HashData(payload.AsSpan(offset, (int)size)));
            offset += (int)size;
        }

        return Convert.ToBase64String(SHA256.HashData([.. digests]));
    }

    private static string Crc32CompositeBySizes(byte[] payload, List<long> sizes)
    {
        var offset = 0;
        var digests = new List<byte>();
        foreach (var size in sizes)
        {
            digests.AddRange(Convert.FromBase64String(S3Fidelity.Crc32B64(payload.AsSpan(offset, (int)size).ToArray())));
            offset += (int)size;
        }

        return S3Fidelity.Crc32B64([.. digests]);
    }

    /// <summary>
    /// Whatever (algorithm, ChecksumType) the installed CLI defaulted to, the stored value must be the
    /// arithmetically correct one for that pair over the real bytes (FULL_OBJECT: a hash of the whole
    /// payload) or the real part split (COMPOSITE: fetched fresh, since only COMPOSITE reports
    /// per-part sizes at all).
    /// </summary>
    private static async Task AssertDefaultChecksumMatches(GetObjectMetadataResponse head, byte[] payload, IAmazonS3 s3, string bucket, string key)
    {
        var ctype = head.ChecksumType?.Value;
        if (head.ChecksumCRC32 is not null)
        {
            string want;
            if (ctype == "COMPOSITE")
            {
                var sizes = await PartSizes(s3, bucket, key);
                Ensure(sizes.Sum() == payload.Length, $"part sizes {Py.Repr(sizes)} do not sum to {payload.Length}");
                want = Crc32CompositeBySizes(payload, sizes) + $"-{sizes.Count}";
            }
            else
            {
                want = S3Fidelity.Crc32B64(payload);
            }

            Ensure(head.ChecksumCRC32 == want, $"default CRC32/{ctype} HeadObject value {Py.Repr(head.ChecksumCRC32)} != computed {Py.Repr(want)}");
            return;
        }

        if (head.ChecksumCRC64NVME is not null)
        {
            Ensure(ctype == "FULL_OBJECT", $"CRC64NVME must be FULL_OBJECT, got ChecksumType={Py.Repr(ctype)}");
            var want = S3Fidelity.Crc64NvmeB64(payload);
            Ensure(head.ChecksumCRC64NVME == want, $"default CRC64NVME HeadObject value {Py.Repr(head.ChecksumCRC64NVME)} != computed {Py.Repr(want)}");
            return;
        }

        throw Fail("plain `aws s3 cp` attached no recognized checksum at all");
    }
}

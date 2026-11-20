// Translated from behavioral/s3/check_deep_s3_immutability_composite_sha256_checksum_reports_parts.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Security.Cryptography;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.deep-s3-immutability-composite-sha256-checksum-reports-parts</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeepS3ImmutabilityCompositeSha256ChecksumReportsParts
{
    /// <summary>The minimum size of a non-final part.</summary>
    private const int Part1Len = 5 * 1024 * 1024;

    /// <summary>4800 bytes.</summary>
    private static readonly byte[] Part2 = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("sha256-tail-", 400)));

    /// <summary>
    /// A SHA256 multipart object stores a COMPOSITE checksum (sha256 of part digests, '-&lt;parts&gt;')
    /// that GetObjectAttributes reports with each part's checksum; a single PUT stores FULL_OBJECT.
    /// </summary>
    [Test]
    [DisplayName("s3.deep-s3-immutability-composite-sha256-checksum-reports-parts")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-imm-");

        var part1 = new byte[Part1Len];
        for (var i = 0; i < Part1Len; i++)
        {
            part1[i] = (byte)(((i * 13) + 5) % 253);
        }

        byte[] whole = [.. part1, .. Part2];

        var up = await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = "multi.bin", ChecksumAlgorithm = ChecksumAlgorithm.SHA256 });
        var uploadId = up.UploadId;
        ctx.OnCleanup(S3Fidelity.AbortQuietly(s3, bucket, "multi.bin", uploadId));
        Ensure(
            up.ChecksumAlgorithm == ChecksumAlgorithm.SHA256 && up.ChecksumType == ChecksumType.COMPOSITE,
            $"CreateMultipartUpload echoed ChecksumAlgorithm {Py.Repr(up.ChecksumAlgorithm?.Value)}, ChecksumType {Py.Repr(up.ChecksumType?.Value)}; want SHA256 / COMPOSITE (the SHA256 default)");

        var parts = new List<PartETag>();
        foreach (var (number, data) in new[] { (1, part1), (2, Part2) })
        {
            var r = await s3.UploadPartAsync(new UploadPartRequest
            {
                BucketName = bucket,
                Key = "multi.bin",
                UploadId = uploadId,
                PartNumber = number,
                InputStream = new MemoryStream(data),
                ChecksumAlgorithm = ChecksumAlgorithm.SHA256,
            });
            Ensure(r.ChecksumSHA256 == S3Fidelity.Sha256B64(data), $"part {number} ChecksumSHA256 {Py.Repr(r.ChecksumSHA256)} != sha256 of its bytes");
            parts.Add(new PartETag { PartNumber = number, ETag = r.ETag, ChecksumSHA256 = r.ChecksumSHA256 });
        }

        var digest = Convert.ToBase64String(SHA256.HashData([.. SHA256.HashData(part1), .. SHA256.HashData(Part2)]));
        var done = await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest { BucketName = bucket, Key = "multi.bin", UploadId = uploadId, PartETags = parts });
        Ensure(
            done.ChecksumSHA256 == $"{digest}-2",
            $"CompleteMultipartUpload ChecksumSHA256 {Py.Repr(done.ChecksumSHA256)}; want sha256(sha256(p1)||sha256(p2)) + '-2' = {Py.Repr(digest + "-2")}");
        Ensure(done.ChecksumType == ChecksumType.COMPOSITE, $"CompleteMultipartUpload ChecksumType {Py.Repr(done.ChecksumType?.Value)}, want 'COMPOSITE'");
        Ensure(digest != S3Fidelity.Sha256B64(whole), "the composite digest collided with the whole-object sha256");

        using (var got = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = "multi.bin", ChecksumMode = ChecksumMode.ENABLED }))
        {
            var body = await S3Fidelity.ReadBody(got);
            Ensure(body.AsSpan().SequenceEqual(whole), "the multipart object's bytes differ from what was uploaded");
            Ensure(
                got.ChecksumSHA256 == $"{digest}-2" && got.ChecksumType == ChecksumType.COMPOSITE,
                $"GetObject reported ChecksumSHA256 {Py.Repr(got.ChecksumSHA256)} / ChecksumType {Py.Repr(got.ChecksumType?.Value)}; want {Py.Repr(digest + "-2")} / COMPOSITE");
        }

        // GetObjectAttributes returns the composite digest without the '-N' suffix other calls carry.
        var attrs = await s3.GetObjectAttributesAsync(new GetObjectAttributesRequest
        {
            BucketName = bucket,
            Key = "multi.bin",
            ObjectAttributes = [ObjectAttributes.Checksum, ObjectAttributes.ObjectParts, ObjectAttributes.ObjectSize],
        });
        Ensure(
            ChecksumView(attrs.Checksum) == $"{{'ChecksumSHA256': '{digest}', 'ChecksumType': 'COMPOSITE'}}",
            $"GetObjectAttributes Checksum {ChecksumView(attrs.Checksum)}; want the composite digest {Py.Repr(digest)} (no '-N' in this view) with ChecksumType COMPOSITE");
        Ensure(attrs.ObjectSize == whole.Length, $"ObjectSize {Py.Repr(attrs.ObjectSize)} != {whole.Length}");
        var op = attrs.ObjectParts;
        Ensure(
            op?.TotalPartsCount == 2 && op.IsTruncated == false,
            $"ObjectParts summary (TotalPartsCount {Py.Repr(op?.TotalPartsCount)}, IsTruncated {Py.Repr(op?.IsTruncated)}); want TotalPartsCount 2, not truncated");
        var listed = (op?.Parts ?? []).Select(p => (p.PartNumber, p.Size, p.ChecksumSHA256)).ToList();
        Ensure(
            listed.SequenceEqual([(1, (long?)part1.Length, S3Fidelity.Sha256B64(part1)), (2, (long?)Part2.Length, S3Fidelity.Sha256B64(Part2))]),
            $"ObjectParts.Parts {string.Join(", ", listed)} do not reproduce the uploaded parts' sizes and sha256s");

        // ObjectParts paginates on PartNumberMarker like ListParts.
        var page1 = (await s3.GetObjectAttributesAsync(new GetObjectAttributesRequest
        {
            BucketName = bucket,
            Key = "multi.bin",
            ObjectAttributes = [ObjectAttributes.ObjectParts],
            MaxParts = 1,
        })).ObjectParts;
        Ensure(
            page1?.IsTruncated == true && (page1.Parts ?? []).Select(p => p.PartNumber).SequenceEqual([1]),
            $"MaxParts=1 page (IsTruncated {Py.Repr(page1?.IsTruncated)}, parts {Py.Repr(page1?.Parts?.Select(p => p.PartNumber))}); want only part 1 and IsTruncated");
        var page2 = (await s3.GetObjectAttributesAsync(new GetObjectAttributesRequest
        {
            BucketName = bucket,
            Key = "multi.bin",
            ObjectAttributes = [ObjectAttributes.ObjectParts],
            MaxParts = 1,
            PartNumberMarker = page1?.NextPartNumberMarker,
        })).ObjectParts;
        Ensure(
            (page2?.Parts ?? []).Select(p => p.PartNumber).SequenceEqual([2]) && page2?.IsTruncated == false,
            $"the page after marker {Py.Repr(page1?.NextPartNumberMarker)} was (IsTruncated {Py.Repr(page2?.IsTruncated)}, parts {Py.Repr(page2?.Parts?.Select(p => p.PartNumber))}); want only part 2");

        // --- Contrast: the identical bytes in ONE PUT store a FULL_OBJECT sha256 of the bytes.
        var single = await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "single.bin", InputStream = new MemoryStream(whole), ChecksumAlgorithm = ChecksumAlgorithm.SHA256 });
        Ensure(single.ChecksumSHA256 == S3Fidelity.Sha256B64(whole), $"single PUT ChecksumSHA256 {Py.Repr(single.ChecksumSHA256)} != sha256 of the bytes");
        var sattrs = await s3.GetObjectAttributesAsync(new GetObjectAttributesRequest
        {
            BucketName = bucket,
            Key = "single.bin",
            ObjectAttributes = [ObjectAttributes.Checksum, ObjectAttributes.ObjectParts],
        });
        Ensure(
            ChecksumView(sattrs.Checksum) == $"{{'ChecksumSHA256': '{S3Fidelity.Sha256B64(whole)}', 'ChecksumType': 'FULL_OBJECT'}}",
            $"GetObjectAttributes on the single PUT reported {ChecksumView(sattrs.Checksum)}; want the stored FULL_OBJECT sha256 of the bytes");
        Ensure(sattrs.ObjectParts is null, $"a single-PUT object reported ObjectParts (TotalPartsCount {Py.Repr(sattrs.ObjectParts?.TotalPartsCount)}); it has no parts");
    }

    /// <summary>boto3's <c>attrs["Checksum"]</c> dict, as Python prints it.</summary>
    private static string ChecksumView(Checksum? checksum)
    {
        if (checksum is null)
        {
            return "None";
        }

        var members = new List<string>();
        void Add(string name, string? value)
        {
            if (value is not null)
            {
                members.Add($"'{name}': '{value}'");
            }
        }

        Add("ChecksumCRC32", checksum.ChecksumCRC32);
        Add("ChecksumCRC32C", checksum.ChecksumCRC32C);
        Add("ChecksumCRC64NVME", checksum.ChecksumCRC64NVME);
        Add("ChecksumSHA1", checksum.ChecksumSHA1);
        Add("ChecksumSHA256", checksum.ChecksumSHA256);
        Add("ChecksumType", checksum.ChecksumType?.Value);
        return "{" + string.Join(", ", members) + "}";
    }
}

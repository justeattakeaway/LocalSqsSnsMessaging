// Translated from behavioral/s3/check_deep_s3_immutability_multipart_etag_is_md5_of_part_md5s.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.deep-s3-immutability-multipart-etag-is-md5-of-part-md5s</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeepS3ImmutabilityMultipartEtagIsMd5OfPartMd5s
{
    /// <summary>The minimum size of a non-final part.</summary>
    private const int Part1Len = 5 * 1024 * 1024;

    /// <summary>5500 bytes; the last part may be any size.</summary>
    private static readonly byte[] Part2 = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("final-part-", 500)));

    /// <summary>
    /// A multipart ETag is exactly md5(concat(part MD5s)) + '-2' while a single PUT of the same bytes
    /// has ETag md5(bytes); a ranged GET across the part boundary returns the identical slice (206,
    /// exact Content-Range) from both objects.
    /// </summary>
    [Test]
    [DisplayName("s3.deep-s3-immutability-multipart-etag-is-md5-of-part-md5s")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-imm-");
        var part1 = new byte[Part1Len];
        for (var i = 0; i < Part1Len; i++)
        {
            part1[i] = (byte)(((i * 7) + 3) % 251);
        }

        byte[] whole = [.. part1, .. Part2];

        var up = await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = "multi.bin" });
        var uploadId = up.UploadId;
        ctx.OnCleanup(S3Fidelity.AbortQuietly(s3, bucket, "multi.bin", uploadId));
        var parts = new List<PartETag>();
        foreach (var (number, data) in new[] { (1, part1), (2, Part2) })
        {
            var r = await s3.UploadPartAsync(new UploadPartRequest { BucketName = bucket, Key = "multi.bin", UploadId = uploadId, PartNumber = number, InputStream = new MemoryStream(data) });
            var want = "\"" + S3Fidelity.Md5Hex(data) + "\"";
            Ensure(r.ETag == want, $"part {number} ETag {Py.Repr(r.ETag)} != md5 of its bytes {Py.Repr(want)}");
            parts.Add(S3Fidelity.Part(number, r.ETag));
        }

        var done = await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest { BucketName = bucket, Key = "multi.bin", UploadId = uploadId, PartETags = parts });
        var composite = S3Fidelity.Md5Hex([.. S3Fidelity.Md5(part1), .. S3Fidelity.Md5(Part2)]) + "-2";
        Ensure(
            done.ETag == $"\"{composite}\"",
            $"CompleteMultipartUpload ETag {Py.Repr(done.ETag)}; want md5(md5(p1)||md5(p2))-2 = {Py.Repr(composite)}");
        var plain = S3Fidelity.Md5Hex(whole);
        Ensure(composite.Split('-')[0] != plain, "the composite digest collided with the plain MD5");
        var head = await s3.GetObjectMetadataAsync(bucket, "multi.bin");
        Ensure(head.ETag == $"\"{composite}\"", $"HeadObject ETag {Py.Repr(head.ETag)} != {Py.Repr(composite)}");
        Ensure(head.ContentLength == whole.Length, $"multipart object is {head.ContentLength} bytes, want {whole.Length}");
        var partHead = await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = "multi.bin", PartNumber = 2 });
        Ensure(
            partHead.ContentLength == Part2.Length && partHead.PartsCount == 2,
            $"HeadObject PartNumber=2 reported ContentLength {partHead.ContentLength}, PartsCount {Py.Repr(partHead.PartsCount)}; want {Part2.Length} and 2");

        // --- The identical bytes in ONE PUT: the ETag is the plain MD5, and differs from the above.
        var single = await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "single.bin", InputStream = new MemoryStream(whole) });
        Ensure(single.ETag == $"\"{plain}\"", $"single-PUT ETag {Py.Repr(single.ETag)}; want the plain MD5 {Py.Repr(plain)}");
        Ensure(Regex.IsMatch(single.ETag, "^\"[0-9a-f]{32}\"$"), $"single-PUT ETag {Py.Repr(single.ETag)} is not a bare 32-hex MD5");
        Ensure(
            single.ETag != done.ETag,
            $"the single-PUT and multipart ETags are equal; S3 ETags depend on how an object was uploaded, not only on its bytes (both {Py.Repr(single.ETag)})");

        // --- Storage identity regardless of ETag: a Range across the part boundary is byte-exact.
        const int lo = Part1Len - 10;
        const int hi = Part1Len + 9;
        var wantSlice = whole.AsSpan(lo, hi - lo + 1).ToArray();
        foreach (var (key, etag) in new[] { ("multi.bin", done.ETag), ("single.bin", single.ETag) })
        {
            using var r = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key, ByteRange = new ByteRange(lo, hi) });
            var got = await S3Fidelity.ReadBody(r);
            Ensure(r.HttpStatusCode == HttpStatusCode.PartialContent, $"{key}: ranged GET returned HTTP {(int)r.HttpStatusCode}, want 206");
            Ensure(r.ContentRange == $"bytes {lo}-{hi}/{whole.Length}", $"{key}: Content-Range {Py.Repr(r.ContentRange)}");
            Ensure(got.AsSpan().SequenceEqual(wantSlice), $"{key}: the range across the part boundary returned {Py.Repr(got)}, want {Py.Repr(wantSlice)}");
            Ensure(r.ETag == etag, $"{key}: ranged GET ETag {Py.Repr(r.ETag)} != the object's {Py.Repr(etag)}");
        }

        var full = await S3Fidelity.GetBytes(s3, bucket, "multi.bin");
        Ensure(
            S3Fidelity.Md5Hex(full) == plain,
            "the multipart object's full bytes do not hash to the single-PUT MD5 — the content differs");
    }
}

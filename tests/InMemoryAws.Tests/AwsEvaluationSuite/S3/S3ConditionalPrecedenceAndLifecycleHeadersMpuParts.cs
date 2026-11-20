// Translated from behavioral/s3/check_s3_conditional_precedence_and_lifecycle_headers_mpu_parts.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3-conditional-precedence-and-lifecycle-headers-mpu-parts</summary>
/// <remarks>The raw headers botocore keeps in <c>ResponseMetadata.HTTPHeaders</c> are read from the response.</remarks>
[Category("AwsEvaluationSuite")]
public sealed class S3ConditionalPrecedenceAndLifecycleHeadersMpuParts
{
    private const int Mib = 1024 * 1024;
    private static readonly int[] Sizes = [(5 * Mib) + 123, (6 * Mib) + 7, 777_001];

    /// <summary>
    /// GetObject with partNumber on a three-part object of unequal parts answers 206 for each part with
    /// x-amz-mp-parts-count 3, the exact Content-Range and Content-Length of that part, and its uploaded
    /// bytes, matching a Range GET over the same span.
    /// </summary>
    [Test]
    [DisplayName("s3.s3-conditional-precedence-and-lifecycle-headers-mpu-parts")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-s3cond-parts-");
        const string key = "parts/three-part.bin";
        var chunks = Sizes.Select((size, i) =>
        {
            var chunk = new byte[size];
            Array.Fill(chunk, (byte)(0x41 + i));
            return chunk;
        }).ToList();
        var total = Sizes.Sum();

        var uploadId = (await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = key })).UploadId;
        ctx.OnCleanup(async () =>
        {
            if (await FidelityContext.CaptureError(() => s3.AbortMultipartUploadAsync(bucket, key, uploadId)) is { Code: not ("NoSuchBucket" or "NoSuchKey" or "NoSuchUpload") } error)
            {
                throw error.Exception;
            }
        });
        var parts = new List<PartETag>();
        for (var number = 1; number <= chunks.Count; number++)
        {
            var etag = (await s3.UploadPartAsync(new UploadPartRequest
            {
                BucketName = bucket,
                Key = key,
                UploadId = uploadId,
                PartNumber = number,
                InputStream = new MemoryStream(chunks[number - 1]),
            })).ETag;
            parts.Add(new PartETag { ETag = etag, PartNumber = number });
        }

        var done = await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest { BucketName = bucket, Key = key, UploadId = uploadId, PartETags = parts });
        var objectEtag = done.ETag;
        Ensure(objectEtag.Trim('"').EndsWith("-3", StringComparison.Ordinal), $"a 3-part object's ETag must end in '-3', got {Py.Repr(objectEtag)}");

        // --- Control: a whole-object GET carries no part framing.
        var (whole, whdrs) = await S3Fidelity.WithHeaders(s3, () => s3.GetObjectAsync(bucket, key));
        using (whole)
        {
            Ensure(whole.HttpStatusCode == System.Net.HttpStatusCode.OK, $"a whole-object GET must be 200, got {(int)whole.HttpStatusCode}");
            var framing = new[] { "x-amz-mp-parts-count", "content-range" }.Where(whdrs.ContainsKey).ToDictionary(h => h, h => whdrs[h], StringComparer.Ordinal);
            Ensure(
                !whdrs.ContainsKey("x-amz-mp-parts-count") && !whdrs.ContainsKey("content-range"),
                $"a whole-object GET must carry neither x-amz-mp-parts-count nor Content-Range, got {Py.Repr(framing)}");
            var wholeBody = await S3Fidelity.ReadBody(whole);
            Ensure(
                SHA256.HashData(wholeBody).AsSpan().SequenceEqual(SHA256.HashData(chunks.SelectMany(c => c).ToArray())),
                $"the reassembled object ({wholeBody.Length} bytes) is not the three parts in order");
        }

        var offset = 0;
        for (var number = 1; number <= chunks.Count; number++)
        {
            var chunk = chunks[number - 1];
            var (start, end) = (offset, offset + chunk.Length - 1);
            offset += chunk.Length;
            var (got, hdrs) = await S3Fidelity.WithHeaders(s3, () => s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key, PartNumber = number }));
            using (got)
            {
                var status = (int)got.HttpStatusCode;
                Ensure(status == 206, $"part {number}: a part-scoped GET must be 206, got {status}");
                Ensure(
                    hdrs.GetValueOrDefault("x-amz-mp-parts-count") == "3",
                    $"part {number}: x-amz-mp-parts-count must be '3', got {Py.Repr(hdrs.GetValueOrDefault("x-amz-mp-parts-count"))}");
                var wantRange = $"bytes {start}-{end}/{total}";
                Ensure(
                    hdrs.GetValueOrDefault("content-range") == wantRange,
                    $"part {number}: Content-Range must be {Py.Repr(wantRange)} (the uploaded byte span), "
                    + $"got {Py.Repr(hdrs.GetValueOrDefault("content-range"))}");
                var contentLength = hdrs.TryGetValue("content-length", out var length) ? int.Parse(length, CultureInfo.InvariantCulture) : -1;
                Ensure(
                    contentLength == chunk.Length,
                    $"part {number}: Content-Length must be {chunk.Length}, got {Py.Repr(hdrs.GetValueOrDefault("content-length"))}");
                Ensure(
                    got.ETag == objectEtag,
                    $"part {number}: a part-scoped GET reports the OBJECT's ETag {Py.Repr(objectEtag)}, got "
                    + $"{Py.Repr(got.ETag)}");
                var body = await S3Fidelity.ReadBody(got);
                Ensure(
                    MD5.HashData(body).AsSpan().SequenceEqual(MD5.HashData(chunk)),
                    $"part {number}: body ({body.Length} bytes, first {Py.Repr(body.Take(4).ToArray())}) is not the uploaded part "
                    + $"({chunk.Length} bytes of {Py.Repr(chunk.Take(1).ToArray())})");
                using var ranged = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key, ByteRange = new ByteRange(start, end) });
                Ensure(
                    (await S3Fidelity.ReadBody(ranged)).AsSpan().SequenceEqual(body),
                    $"part {number}: the bytes at Range {start}-{end} differ from the partNumber read");
            }
        }

        Ensure(offset == total, $"part offsets summed to {offset}, object is {total} bytes");
    }
}

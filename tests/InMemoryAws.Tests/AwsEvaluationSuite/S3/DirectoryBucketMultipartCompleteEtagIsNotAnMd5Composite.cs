// Translated from behavioral/s3/check_directory_bucket_multipart_complete_etag_is_not_an_md5_composite.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.directory-bucket-multipart-complete-etag-is-not-an-md5-composite</summary>
[Category("AwsEvaluationSuite")]
public sealed class DirectoryBucketMultipartCompleteEtagIsNotAnMd5Composite
{
    private const string Check = "directory-bucket-multipart-complete-etag-is-not-an-md5-composite";

    /// <summary>The minimum size of a non-final part.</summary>
    private const int Part1Length = 5 * 1024 * 1024;

    /// <summary>5500 bytes; the last part may be any size.</summary>
    private static readonly byte[] Part2 = [.. Enumerable.Repeat("final-part-"u8.ToArray(), 500).SelectMany(b => b)];

    /// <summary>
    /// A multipart upload completes on a directory bucket, but its ETag is not the
    /// md5(concat(part MD5s))-N composite a general-purpose bucket's multipart ETag always is.
    /// </summary>
    [Test]
    [DisplayName("s3.directory-bucket-multipart-complete-etag-is-not-an-md5-composite")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var (directory, _) = await S3Fidelity.CreateDirectoryBucket(ctx, s3, FidelityContext.UniqueName("sim2real-s3x-mpu-etag-"), S3Fidelity.ExpressZoneIds[ctx.Region]);
        var twin = await S3Fidelity.GeneralPurposeBucket(ctx, s3, "sim2real-s3x-mpu-etag-gp-", Check);
        var part1 = new byte[Part1Length];
        for (var i = 0; i < part1.Length; i++)
        {
            part1[i] = (byte)(((i * 7) + 3) % 251);
        }

        byte[] whole = [.. part1, .. Part2];
        var twinETag = await CompleteTwoPartUpload(ctx, s3, twin, part1, Part2);
        var composite = Convert.ToHexStringLower(MD5.HashData([.. MD5.HashData(part1), .. MD5.HashData(Part2)])) + "-2";
        var wantTwinETag = $"\"{composite}\"";
        Ensure(
            twinETag == wantTwinETag,
            $"general-purpose twin's multipart ETag was {Py.Repr(twinETag)}, want the md5-composite {Py.Repr(wantTwinETag)} — the control itself is broken");

        var dirETag = await CompleteTwoPartUpload(ctx, s3, directory, part1, Part2);

        // md5(md5(part)||md5(part)) is a pure function of the bytes, independent of the bucket that
        // stored them; the twin proved it equals want_twin_etag, so a differing directory-bucket ETag
        // for the IDENTICAL bytes proves it is not the MD5-of-parts value, regardless of its shape.
        Ensure(
            dirETag != wantTwinETag,
            $"the directory bucket's multipart ETag {Py.Repr(dirETag)} equals md5(md5(p1)||md5(p2))-2 {Py.Repr(wantTwinETag)} for the identical bytes; want it to differ");
        var head = await s3.GetObjectMetadataAsync(directory, "multi.bin");
        Ensure(head.ETag == dirETag, $"HeadObject ETag {Py.Repr(head.ETag)} != CompleteMultipartUpload's {Py.Repr(dirETag)}");
        Ensure(head.ContentLength == whole.Length, $"directory bucket multipart object is {head.ContentLength} bytes, want {whole.Length}");
        var body = await S3Fidelity.GetBytes(s3, directory, "multi.bin");
        Ensure(body.SequenceEqual(whole), "the directory bucket's multipart object did not reassemble the parts byte-for-byte");
    }

    /// <summary>CreateMultipartUpload, upload two parts, CompleteMultipartUpload; return its ETag.</summary>
    private static async Task<string> CompleteTwoPartUpload(FidelityContext ctx, IAmazonS3 s3, string bucket, byte[] part1, byte[] part2)
    {
        var up = await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = "multi.bin" });
        var uploadId = up.UploadId;
        ctx.OnCleanup(S3Fidelity.AbortQuietly(s3, bucket, "multi.bin", uploadId));
        var parts = new List<PartETag>();
        foreach (var (number, data) in new[] { (1, part1), (2, part2) })
        {
            var r = await s3.UploadPartAsync(new UploadPartRequest { BucketName = bucket, Key = "multi.bin", UploadId = uploadId, PartNumber = number, InputStream = new MemoryStream(data) });
            parts.Add(new PartETag { PartNumber = number, ETag = r.ETag });
        }

        var done = await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest { BucketName = bucket, Key = "multi.bin", UploadId = uploadId, PartETags = parts });
        return done.ETag;
    }
}

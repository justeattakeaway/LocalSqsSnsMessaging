// Translated from behavioral/s3/check_get_object_attributes_multipart_etag_is_part_md5_composite.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.get-object-attributes-multipart-etag-is-part-md5-composite</summary>
[Category("AwsEvaluationSuite")]
public sealed class GetObjectAttributesMultipartEtagIsPartMd5Composite
{
    /// <summary>The minimum size of a non-final part.</summary>
    private const int Part1Len = 5 * 1024 * 1024;

    /// <summary>3400 B filler; the last part may be any size.</summary>
    private static readonly byte[] Part2 = [.. Enumerable.Range(0, 3400).Select(i => (byte)(((i * 251) + 97) % 256))];

    /// <summary>
    /// GetObjectAttributes' ETag for a 2-part multipart object is md5(md5(part1)||md5(part2))+'-2',
    /// equal to HeadObject's ETag with its quotes stripped; the same bytes in one PUT report a bare MD5.
    /// </summary>
    [Test]
    [DisplayName("s3.get-object-attributes-multipart-etag-is-part-md5-composite")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-goaetag-");
        const string multiKey = "multi.bin";
        const string singleKey = "single.bin";

        var part1 = new byte[Part1Len];
        for (var i = 0; i < Part1Len; i++)
        {
            part1[i] = (byte)(((i * 17) + 11) % 256);
        }

        byte[] whole = [.. part1, .. Part2];

        var up = await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = multiKey });
        var uploadId = up.UploadId;
        ctx.OnCleanup(S3Fidelity.AbortQuietly(s3, bucket, multiKey, uploadId));

        var parts = new List<PartETag>();
        foreach (var (number, data) in new[] { (1, part1), (2, Part2) })
        {
            var r = await s3.UploadPartAsync(new UploadPartRequest
            {
                BucketName = bucket,
                Key = multiKey,
                UploadId = uploadId,
                PartNumber = number,
                InputStream = new MemoryStream(data),
            });
            parts.Add(new PartETag { PartNumber = number, ETag = r.ETag });
        }

        var done = await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest { BucketName = bucket, Key = multiKey, UploadId = uploadId, PartETags = parts });
        var composite = S3Fidelity.MultipartETag(part1, Part2).Trim('"');
        Ensure(done.ETag == $"\"{composite}\"", $"CompleteMultipartUpload ETag {Py.Repr(done.ETag)}; want md5(md5(p1)||md5(p2))+'-2' = {Py.Repr(composite)}");

        var head = await s3.GetObjectMetadataAsync(bucket, multiKey);
        Ensure(head.ETag == $"\"{composite}\"", $"HeadObject ETag {Py.Repr(head.ETag)} != {Py.Repr(composite)}");

        var multiAttrs = await s3.GetObjectAttributesAsync(new GetObjectAttributesRequest { BucketName = bucket, Key = multiKey, ObjectAttributes = [ObjectAttributes.ETag] });
        Ensure(multiAttrs.ETag == composite, $"GetObjectAttributes ETag {Py.Repr(multiAttrs.ETag)} != md5(md5(p1)||md5(p2))+'-2' = {Py.Repr(composite)}");
        Ensure(
            multiAttrs.ETag == head.ETag.Trim('"'),
            $"GetObjectAttributes ETag {Py.Repr(multiAttrs.ETag)} != HeadObject ETag with quotes stripped {Py.Repr(head.ETag.Trim('"'))}");

        // --- The identical bytes in ONE PUT: a bare MD5, differing from the multipart composite above.
        var single = await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = singleKey, InputStream = new MemoryStream(whole) });
        var plain = S3Fidelity.Md5Hex(whole);
        Ensure(single.ETag == $"\"{plain}\"", $"single-PUT ETag {Py.Repr(single.ETag)}; want the plain MD5 {Py.Repr(plain)}");

        var singleHead = await s3.GetObjectMetadataAsync(bucket, singleKey);
        Ensure(singleHead.ETag == $"\"{plain}\"", $"HeadObject ETag {Py.Repr(singleHead.ETag)} != {Py.Repr(plain)}");

        var singleAttrs = await s3.GetObjectAttributesAsync(new GetObjectAttributesRequest { BucketName = bucket, Key = singleKey, ObjectAttributes = [ObjectAttributes.ETag] });
        Ensure(singleAttrs.ETag == plain, $"GetObjectAttributes ETag {Py.Repr(singleAttrs.ETag)} != the plain MD5 {Py.Repr(plain)}");
        Ensure(
            singleAttrs.ETag == singleHead.ETag.Trim('"'),
            $"GetObjectAttributes ETag {Py.Repr(singleAttrs.ETag)} != HeadObject ETag with quotes stripped {Py.Repr(singleHead.ETag.Trim('"'))}");
        Ensure(
            singleAttrs.ETag != multiAttrs.ETag,
            $"the single-PUT and multipart GetObjectAttributes ETags are equal; S3 ETags depend on how an object was uploaded, not only on its bytes (both {Py.Repr(singleAttrs.ETag)})");
    }
}

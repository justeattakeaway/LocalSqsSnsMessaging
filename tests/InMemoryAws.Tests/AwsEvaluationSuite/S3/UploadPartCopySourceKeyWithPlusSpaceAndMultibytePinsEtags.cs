// Translated from behavioral/s3/check_upload_part_copy_source_key_with_plus_space_and_multibyte_pins_etags.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.upload-part-copy-source-key-with-plus-space-and-multibyte-pins-etags</summary>
[Category("AwsEvaluationSuite")]
public sealed class UploadPartCopySourceKeyWithPlusSpaceAndMultibytePinsEtags
{
    private const int Part1Size = 5 * 1024 * 1024;
    private const int Part2Size = 4096;

    private static readonly (string? Code, string? Message) NoSuchKey = ("NoSuchKey", "The specified key does not exist.");

    /// <summary>
    /// UploadPartCopy on a multibyte-plus-space source key pins each part's ETag and the
    /// completed ETag to their MD5 composite; spelling the plus as a space in CopySource is
    /// refused NoSuchKey with no part recorded, and the correctly spelled key still completes.
    /// </summary>
    [Test]
    [DisplayName("s3.upload-part-copy-source-key-with-plus-space-and-multibyte-pins-etags")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-pluscopy-");

        const string srcKey = "docs/café menu+specials.bin";
        var body = S3Fidelity.RandomBytes(Part1Size + Part2Size);
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = srcKey, InputStream = new MemoryStream(body) });

        var part1Expected = S3Fidelity.Md5ETag(body[..Part1Size]);
        var part2Expected = S3Fidelity.Md5ETag(body[Part1Size..]);

        // === success path: two ranges of the real key assemble into a new multipart object ===
        const string destKey = "dest/assembled.bin";
        var uploadId = (await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = destKey })).UploadId;
        ctx.OnCleanup(S3Fidelity.AbortQuietly(s3, bucket, destKey, uploadId));

        var etag1 = (await s3.CopyPartAsync(CopyPart(bucket, srcKey, destKey, uploadId, 1, 0, Part1Size - 1))).ETag;
        Ensure(etag1 == part1Expected, $"UploadPartCopy part 1 ETag {Py.Repr(etag1)}, expected the MD5 of its range {Py.Repr(part1Expected)}");

        var etag2 = (await s3.CopyPartAsync(CopyPart(bucket, srcKey, destKey, uploadId, 2, Part1Size, Part1Size + Part2Size - 1))).ETag;
        Ensure(etag2 == part2Expected, $"UploadPartCopy part 2 ETag {Py.Repr(etag2)}, expected the MD5 of its range {Py.Repr(part2Expected)}");

        var expectedFinal = CompositeETag([etag1, etag2]);
        var complete = await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket,
            Key = destKey,
            UploadId = uploadId,
            PartETags = [S3Fidelity.Part(1, etag1), S3Fidelity.Part(2, etag2)],
        });
        var finalEtag = complete.ETag;
        Ensure(finalEtag == expectedFinal, $"CompleteMultipartUpload ETag {Py.Repr(finalEtag)}, expected {Py.Repr(expectedFinal)}");
        Ensure(finalEtag.EndsWith("-2\"", StringComparison.Ordinal), $"CompleteMultipartUpload ETag {Py.Repr(finalEtag)} does not end '-2'");

        var got = await S3Fidelity.GetBytes(s3, bucket, destKey);
        Ensure(got.AsSpan().SequenceEqual(body), $"assembled object is {got.Length} bytes, expected {body.Length} matching the source");

        // === refusal: the same copy with the '+' spelled as a space targets a key that never existed ===
        var badKey = srcKey.Replace('+', ' ');
        Ensure(badKey != srcKey, "the substitution produced the same key, which defeats the probe");

        const string destKey2 = "dest/rejected.bin";
        var uploadId2 = (await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = destKey2 })).UploadId;
        ctx.OnCleanup(S3Fidelity.AbortQuietly(s3, bucket, destKey2, uploadId2));

        var refused = await S3Fidelity.ErrorPair(() => s3.CopyPartAsync(CopyPart(bucket, badKey, destKey2, uploadId2, 1)));
        Ensure(refused == NoSuchKey, $"UploadPartCopy with the plus spelled as a space returned {S3Fidelity.Repr(refused)}, expected {S3Fidelity.Repr(NoSuchKey)}");

        var parts = (await s3.ListPartsAsync(new ListPartsRequest { BucketName = bucket, Key = destKey2, UploadId = uploadId2 })).Parts ?? [];
        Ensure(parts.Count == 0, $"a rejected UploadPartCopy recorded a part anyway: {Py.Repr(parts.Select(p => p.PartNumber).ToList())}");

        // Positive control in the same upload: the correctly spelled key still completes it.
        var etagCtrl = (await s3.CopyPartAsync(CopyPart(bucket, srcKey, destKey2, uploadId2, 1))).ETag;
        var expectedCtrl = S3Fidelity.Md5ETag(body);
        Ensure(etagCtrl == expectedCtrl, $"UploadPartCopy of the correctly spelled key returned {Py.Repr(etagCtrl)}, expected {Py.Repr(expectedCtrl)}");

        var expectedFinal2 = CompositeETag([etagCtrl]);
        var complete2 = await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket,
            Key = destKey2,
            UploadId = uploadId2,
            PartETags = [S3Fidelity.Part(1, etagCtrl)],
        });
        Ensure(complete2.ETag == expectedFinal2, $"CompleteMultipartUpload ETag {Py.Repr(complete2.ETag)}, expected {Py.Repr(expectedFinal2)}");

        var got2 = await S3Fidelity.GetBytes(s3, bucket, destKey2);
        Ensure(got2.AsSpan().SequenceEqual(body), $"the surviving upload assembled {got2.Length} bytes, expected {body.Length} matching the source");
    }

    /// <summary><c>s3.upload_part_copy(..., CopySource={"Bucket": bucket, "Key": source}, CopySourceRange=...)</c>.</summary>
    private static CopyPartRequest CopyPart(string bucket, string source, string destination, string uploadId, int partNumber, long? first = null, long? last = null) => new()
    {
        SourceBucket = bucket,
        SourceKey = source,
        DestinationBucket = bucket,
        DestinationKey = destination,
        UploadId = uploadId,
        PartNumber = partNumber,
        FirstByte = first,
        LastByte = last,
    };

    /// <summary><c>_composite_etag(part_etags, n)</c>: the MD5 of the parts' binary MD5s, then <c>-n</c>, quoted.</summary>
    private static string CompositeETag(IReadOnlyList<string> partEtags)
    {
        byte[] digests = [.. partEtags.SelectMany(e => Convert.FromHexString(e.Trim('"')))];
        return $"\"{S3Fidelity.Md5Hex(digests)}-{partEtags.Count}\"";
    }
}

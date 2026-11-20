// Translated from behavioral/s3/check_upload_part_copy_source_if_match_takes_a_composite_etag_and_refuses_it_once_stale.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Security.Cryptography;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.upload-part-copy-source-if-match-takes-a-composite-etag-and-refuses-it-once-stale</summary>
[Category("AwsEvaluationSuite")]
public sealed class UploadPartCopySourceIfMatchTakesACompositeEtagAndRefusesItOnceStale
{
    private const string Check = "upload-part-copy-source-if-match-takes-a-composite-etag-and-refuses-it-once-stale";
    private const int Part = 5 * 1024 * 1024;
    private static readonly (string?, string?, int) Precondition = ("PreconditionFailed", "At least one of the pre-conditions you specified did not hold", 412);

    /// <summary>
    /// UploadPartCopy with CopySourceIfMatch accepts a multipart source's composite ETag and copies
    /// exactly the addressed byte range; once the source is overwritten, the same ETag is refused 412
    /// PreconditionFailed and adds no part to the destination upload.
    /// Reference: https://docs.aws.amazon.com/AmazonS3/latest/API/API_UploadPartCopy.html
    /// </summary>
    [Test]
    [DisplayName("s3.upload-part-copy-source-if-match-takes-a-composite-etag-and-refuses-it-once-stale")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateTaggedBucket(ctx, "sim2real", Check, "sim2real-behav-bkt-");
        const string src = "source/large.bin";
        const string dst = "copies/large.bin";

        byte[][] parts = [S3Fidelity.RandomBytes(Part), S3Fidelity.RandomBytes(Part + RandomNumberGenerator.GetInt32(2048))];
        var composite = await S3Fidelity.MultipartPut(ctx, s3, bucket, src, parts);
        Ensure(composite == S3Fidelity.MultipartETag(parts), $"source ETag {Py.Repr(composite)}, expected {Py.Repr(S3Fidelity.MultipartETag(parts))}");
        var whole = parts.SelectMany(p => p).ToArray();

        // Copy a range spanning the source's part boundary, conditioned on its composite ETag.
        var start = Part - 1 - RandomNumberGenerator.GetInt32(Part / 2);
        // PART + 1 bytes starting just below the 5 MiB boundary, so the range spans both source parts.
        var end = start + Part;
        var copyUpload = await S3Fidelity.StartUpload(ctx, s3, bucket, dst);
        var copied = (await s3.CopyPartAsync(new CopyPartRequest
        {
            DestinationBucket = bucket,
            DestinationKey = dst,
            UploadId = copyUpload,
            PartNumber = 1,
            SourceBucket = bucket,
            SourceKey = src,
            FirstByte = start,
            LastByte = end,
            ETagToMatch = [composite],
        })).ETag;
        var wantPart = whole[start..(end + 1)];
        Ensure(copied == S3Fidelity.Md5ETag(wantPart), $"UploadPartCopy part ETag {Py.Repr(copied)} is not the MD5 of source bytes {start}-{end}");

        // Overwrite the source: its composite ETag is stale, and the conditional copy is refused.
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = src, ContentBody = "replaced-source" });
        var lateError = await S3Fidelity.ExpectError(
            () => s3.CopyPartAsync(new CopyPartRequest
            {
                DestinationBucket = bucket,
                DestinationKey = dst,
                UploadId = copyUpload,
                PartNumber = 2,
                SourceBucket = bucket,
                SourceKey = src,
                ETagToMatch = [composite],
            }),
            "the call SUCCEEDED where real AWS refuses it");
        var late = (lateError.Code, lateError.Message, (int)lateError.StatusCode);
        Ensure(late == Precondition, $"UploadPartCopy on a stale composite ETag: expected {Precondition}, got {late}");
        var listed = (await s3.ListPartsAsync(new ListPartsRequest { BucketName = bucket, Key = dst, UploadId = copyUpload })).Parts ?? [];
        var got = listed.Select(p => (p.PartNumber ?? 0, p.Size ?? 0, p.ETag)).ToList();
        Ensure(got.SequenceEqual([(1, (long)wantPart.Length, copied)]), $"after the refusal ListParts reports {Py.Repr(got.Select(g => g.ToString()).ToList())}");

        var final = await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket,
            Key = dst,
            UploadId = copyUpload,
            PartETags = [S3Fidelity.Part(1, copied)],
        });
        Ensure(final.ETag == S3Fidelity.MultipartETag(wantPart), $"copy ETag {Py.Repr(final.ETag)}, expected {Py.Repr(S3Fidelity.MultipartETag(wantPart))}");
        var body = await S3Fidelity.GetBytes(s3, bucket, dst);
        Ensure(
            body.AsSpan().SequenceEqual(wantPart),
            $"the copied object holds {body.Length} bytes (md5 {S3Fidelity.Md5Hex(body)}), expected source bytes {start}-{end}: {wantPart.Length} bytes (md5 {S3Fidelity.Md5Hex(wantPart)})");
    }
}

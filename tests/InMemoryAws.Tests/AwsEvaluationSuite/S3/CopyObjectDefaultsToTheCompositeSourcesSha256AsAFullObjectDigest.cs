// Translated from behavioral/s3/check_copy_object_defaults_to_the_composite_sources_sha256_as_a_full_object_digest.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.copy-object-defaults-to-the-composite-sources-sha256-as-a-full-object-digest</summary>
[Category("AwsEvaluationSuite")]
public sealed class CopyObjectDefaultsToTheCompositeSourcesSha256AsAFullObjectDigest
{
    /// <summary>S3's floor for a non-final multipart part.</summary>
    private const int Lead = 5 * 1024 * 1024;
    private const int Tail = 768 * 1024;

    /// <summary>
    /// CopyObject of a two-part SHA256 COMPOSITE multipart source, given no ChecksumAlgorithm of
    /// its own, keeps SHA256 but hashes the destination as one FULL_OBJECT digest over the assembled
    /// bytes, with a fresh, unsuffixed MD5 ETag; the source keeps its own composite digest.
    /// </summary>
    [Test]
    [DisplayName("s3.copy-object-defaults-to-the-composite-sources-sha256-as-a-full-object-digest")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-cpdf-");

        var lead = S3Fidelity.RandomBytes(Lead);
        var tail = S3Fidelity.RandomBytes(Tail);
        byte[] whole = [.. lead, .. tail];

        var upload = await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = bucket,
            Key = "source",
            ChecksumAlgorithm = ChecksumAlgorithm.SHA256,
        });
        var uploadId = upload.UploadId;
        ctx.OnCleanup(S3Fidelity.AbortQuietly(s3, bucket, "source", uploadId));

        var completedParts = new List<PartETag>();
        foreach (var (number, piece) in new[] { (1, lead), (2, tail) })
        {
            var put = await s3.UploadPartAsync(new UploadPartRequest
            {
                BucketName = bucket,
                Key = "source",
                UploadId = uploadId,
                PartNumber = number,
                InputStream = new MemoryStream(piece),
                ChecksumAlgorithm = ChecksumAlgorithm.SHA256,
            });
            completedParts.Add(new PartETag { PartNumber = number, ETag = put.ETag, ChecksumSHA256 = put.ChecksumSHA256 });
        }

        var source = await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket,
            Key = "source",
            UploadId = uploadId,
            PartETags = completedParts,
        });
        Ensure(
            source.ChecksumType?.Value == "COMPOSITE",
            $"completing a 2-part SHA256 multipart upload must report ChecksumType 'COMPOSITE', got {Py.Repr(source.ChecksumType?.Value)}");
        var sourceEtag = source.ETag;
        Ensure(sourceEtag.Trim('"').EndsWith("-2", StringComparison.Ordinal), $"a composite 2-part ETag carries the '-2' part-count suffix; got {Py.Repr(sourceEtag)}");

        // --- The copy names no ChecksumAlgorithm at all.
        var outcome = await s3.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = bucket,
            SourceKey = "source",
            DestinationBucket = bucket,
            DestinationKey = "copy",
        });
        var wantSha256 = S3Fidelity.Sha256B64(whole);
        var wantEtag = S3Fidelity.Md5ETag(whole);

        Ensure(
            outcome.ChecksumType?.Value == "FULL_OBJECT",
            $"an unqualified copy of a composite source must report ChecksumType 'FULL_OBJECT', got {Py.Repr(outcome.ChecksumType?.Value)} — the destination has no parts to be composite over");
        Ensure(
            outcome.ChecksumSHA256 == wantSha256,
            $"CopyObjectResult.ChecksumSHA256 is {Py.Repr(outcome.ChecksumSHA256)}; hashing the {whole.Length} assembled bytes with SHA-256 gives {Py.Repr(wantSha256)}");
        Ensure(outcome.ETag == wantEtag, $"CopyObjectResult.ETag is {Py.Repr(outcome.ETag)}; expected the plain MD5 {Py.Repr(wantEtag)} with no part-count suffix");
        Ensure(
            outcome.ETag != sourceEtag,
            $"the copy's ETag {Py.Repr(outcome.ETag)} must not equal the source's suffixed ETag {Py.Repr(sourceEtag)}: a FULL_OBJECT copy mints its own representation");

        // --- The write survives a read, under both digest-bearing operations.
        var copyHead = await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = "copy", ChecksumMode = ChecksumMode.ENABLED });
        Ensure(
            (copyHead.ChecksumType?.Value, copyHead.ChecksumSHA256, copyHead.ETag) == ("FULL_OBJECT", wantSha256, wantEtag),
            $"HeadObject on the copy reported ({Py.Repr(copyHead.ChecksumType?.Value)}, {Py.Repr(copyHead.ChecksumSHA256)}, {Py.Repr(copyHead.ETag)}); want ('FULL_OBJECT', {Py.Repr(wantSha256)}, {Py.Repr(wantEtag)})");
        var copyAttrs = await s3.GetObjectAttributesAsync(new GetObjectAttributesRequest
        {
            BucketName = bucket,
            Key = "copy",
            ObjectAttributes = [ObjectAttributes.Checksum, ObjectAttributes.ObjectParts, ObjectAttributes.ObjectSize],
        });
        var checksum = S3Fidelity.ChecksumDict(copyAttrs.Checksum);
        Ensure(
            Py.Equal(checksum, new Dictionary<string, object?> { ["ChecksumSHA256"] = wantSha256, ["ChecksumType"] = "FULL_OBJECT" }),
            $"GetObjectAttributes on the copy reported {Py.Repr(checksum)}; want the whole-object SHA-256 {Py.Repr(wantSha256)} under ChecksumType 'FULL_OBJECT'");
        Ensure(
            copyAttrs.ObjectParts is null,
            $"the copy reported ObjectParts {Py.Repr(copyAttrs.ObjectParts?.Parts?.Select(S3Fidelity.PartDict).ToList())}; a FULL_OBJECT copy has no parts to enumerate");
        Ensure(copyAttrs.ObjectSize == whole.Length, $"GetObjectAttributes reported ObjectSize {Py.Repr(copyAttrs.ObjectSize)}, want {whole.Length}");
        var copyBody = await S3Fidelity.GetBytes(s3, bucket, "copy");
        Ensure(copyBody.AsSpan().SequenceEqual(whole), "the copy's bytes differ from the multipart source's assembled bytes");

        // --- The copy leaves the source object unchanged.
        var sourceAfter = await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = "source", ChecksumMode = ChecksumMode.ENABLED });
        Ensure(
            (sourceAfter.ChecksumType?.Value, sourceAfter.ETag) == ("COMPOSITE", sourceEtag),
            $"the source changed after being copied FROM: ({Py.Repr(sourceAfter.ChecksumType?.Value)}, {Py.Repr(sourceAfter.ETag)}), want ('COMPOSITE', {Py.Repr(sourceEtag)})");
    }
}

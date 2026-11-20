// Translated from behavioral/s3/check_deep_s3_immutability_upload_part_wrong_sha256_rejected_slot_stays_empty.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.deep-s3-immutability-upload-part-wrong-sha256-rejected-slot-stays-empty</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeepS3ImmutabilityUploadPartWrongSha256RejectedSlotStaysEmpty
{
    /// <summary>The minimum size of a non-final part.</summary>
    private const int Part1Len = 5 * 1024 * 1024;

    private static readonly byte[] Part2 = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("the-true-second-part-", 200)));

    /// <summary>
    /// An UploadPart whose declared SHA256 does not match its bytes is rejected with BadDigest and
    /// leaves its slot empty in ListParts; the slot then accepts the true digest, and the completed
    /// object is built only from the parts that verified.
    /// </summary>
    [Test]
    [DisplayName("s3.deep-s3-immutability-upload-part-wrong-sha256-rejected-slot-stays-empty")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateTaggedBucket(ctx, "sim2real-behav", "true");
        const string key = "verified.bin";
        var part1 = Enumerable.Range(0, Part1Len).Select(i => (byte)(((i * 11) + 1) % 241)).ToArray();

        var up = await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = key, ChecksumAlgorithm = ChecksumAlgorithm.SHA256 });
        var uploadId = up.UploadId;
        ctx.OnCleanup(S3Fidelity.AbortQuietly(s3, bucket, key, uploadId));

        var r1 = await s3.UploadPartAsync(new UploadPartRequest
        {
            BucketName = bucket,
            Key = key,
            UploadId = uploadId,
            PartNumber = 1,
            InputStream = new MemoryStream(part1),
            ChecksumSHA256 = S3Fidelity.Sha256B64(part1),
        });
        Ensure(r1.ChecksumSHA256 == S3Fidelity.Sha256B64(part1), $"part 1 echoed ChecksumSHA256 {Py.Repr(r1.ChecksumSHA256)}, want its true sha256");

        // A valid sha256, but of other bytes.
        var wrong = S3Fidelity.Sha256B64(Encoding.UTF8.GetBytes("these are not the bytes being sent"));
        var (code, message) = await S3Fidelity.ErrorPair(
            () => s3.UploadPartAsync(new UploadPartRequest
            {
                BucketName = bucket,
                Key = key,
                UploadId = uploadId,
                PartNumber = 2,
                InputStream = new MemoryStream(Part2),
                ChecksumSHA256 = wrong,
            }),
            "an UploadPart whose declared ChecksumSHA256 does not match its bytes unexpectedly SUCCEEDED (HTTP 200) where real AWS refuses it");
        Ensure(code == "BadDigest", $"expected BadDigest for a mismatched part sha256, got {Py.Repr(code)}");
        const string want = "The SHA256 you specified did not match the calculated checksum.";
        Ensure(message == want, $"expected {Py.Repr(want)}, got {Py.Repr(message)}");

        // Effect absent: the refused part never occupied slot 2.
        var listed = await S3Fidelity.ListedPartsSha256(s3, bucket, key, uploadId);
        Ensure(listed.SequenceEqual([(1, S3Fidelity.Sha256B64(part1))]), $"after the rejected part 2, ListParts shows {Py.Repr(listed.Select(p => p.ToString()).ToList())}; want only part 1");

        // Positive control: the same slot accepts the true digest.
        var r2 = await s3.UploadPartAsync(new UploadPartRequest
        {
            BucketName = bucket,
            Key = key,
            UploadId = uploadId,
            PartNumber = 2,
            InputStream = new MemoryStream(Part2),
            ChecksumSHA256 = S3Fidelity.Sha256B64(Part2),
        });
        listed = await S3Fidelity.ListedPartsSha256(s3, bucket, key, uploadId);
        Ensure(
            listed.SequenceEqual([(1, S3Fidelity.Sha256B64(part1)), (2, S3Fidelity.Sha256B64(Part2))]),
            $"after the corrected part 2, ListParts shows {Py.Repr(listed.Select(p => p.ToString()).ToList())}; want parts 1 and 2 with true digests");

        await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket,
            Key = key,
            UploadId = uploadId,
            PartETags =
            [
                new PartETag { PartNumber = 1, ETag = r1.ETag, ChecksumSHA256 = r1.ChecksumSHA256 },
                new PartETag { PartNumber = 2, ETag = r2.ETag, ChecksumSHA256 = r2.ChecksumSHA256 },
            ],
        });
        var body = await S3Fidelity.GetBytes(s3, bucket, key);
        Ensure(
            body.AsSpan().SequenceEqual([.. part1, .. Part2]),
            $"the completed object is {body.Length} bytes and differs from part1+part2 ({part1.Length + Part2.Length} bytes) — the refused part must contribute nothing");
    }
}

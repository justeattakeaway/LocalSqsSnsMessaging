// Translated from behavioral/s3/check_multipart_complete_part_ordering_enforced.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.multipart-complete-part-ordering-enforced</summary>
[Category("AwsEvaluationSuite")]
public sealed class MultipartCompletePartOrderingEnforced
{
    /// <summary>5 MiB minimum for a non-final multipart part.</summary>
    private const int PartSize = 5 * 1024 * 1024;

    /// <summary>
    /// CompleteMultipartUpload rejects an out-of-order part list with InvalidPartOrder and a
    /// never-uploaded part with InvalidPart (exact messages), both parts stay listed, and the
    /// ascending, matching list assembles part1 + part2.
    /// </summary>
    [Test]
    [DisplayName("s3.multipart-complete-part-ordering-enforced")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-s3-");
        const string key = "multipart-object";
        var uploadId = (await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = key })).UploadId;
        ctx.OnCleanup(S3Fidelity.AbortQuietly(s3, bucket, key, uploadId));

        var part1Body = S3Fidelity.Repeat('1', PartSize);
        var part2Body = S3Fidelity.Repeat('2', 1 * 1024 * 1024);
        var etag1 = (await s3.UploadPartAsync(new UploadPartRequest { BucketName = bucket, Key = key, PartNumber = 1, UploadId = uploadId, InputStream = new MemoryStream(part1Body) })).ETag;
        var etag2 = (await s3.UploadPartAsync(new UploadPartRequest { BucketName = bucket, Key = key, PartNumber = 2, UploadId = uploadId, InputStream = new MemoryStream(part2Body) })).ETag;

        var listed = (await s3.ListPartsAsync(new ListPartsRequest { BucketName = bucket, Key = key, UploadId = uploadId })).Parts ?? [];
        var listedByNumber = listed.ToDictionary(p => p.PartNumber ?? 0, p => p.ETag);
        Ensure(
            listedByNumber.Count == 2 && listedByNumber.GetValueOrDefault(1) == etag1 && listedByNumber.GetValueOrDefault(2) == etag2,
            $"ListParts mismatch: {Py.Repr(listedByNumber)}");

        // 1) Out-of-order part list must be rejected.
        var (orderCode, orderMessage) = await S3Fidelity.ErrorPair(() => S3Fidelity.CompleteInListedOrder(ctx, bucket, key, uploadId, [S3Fidelity.Part(2, etag2), S3Fidelity.Part(1, etag1)]));
        Ensure(orderCode == "InvalidPartOrder", $"expected InvalidPartOrder, got {Py.Repr(orderCode)}");
        Ensure(orderMessage == "The list of parts was not in ascending order. Parts must be ordered by part number.", orderMessage ?? "None");

        // 2) A part list referencing a PartNumber that was never uploaded must be rejected.
        var (missingCode, missingMessage) = await S3Fidelity.ErrorPair(() => s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket,
            Key = key,
            UploadId = uploadId,
            PartETags = [S3Fidelity.Part(1, etag1), S3Fidelity.Part(3, "\"00000000000000000000000000000000\"")],
        }));
        Ensure(missingCode == "InvalidPart", $"expected InvalidPart, got {Py.Repr(missingCode)}");
        Ensure(missingMessage == "One or more of the specified parts could not be found.  The part may not have been uploaded, or the specified entity tag may not match the part's entity tag.", missingMessage ?? "None");

        // The upload must still be alive after both rejected attempts.
        var stillListed = (await s3.ListPartsAsync(new ListPartsRequest { BucketName = bucket, Key = key, UploadId = uploadId })).Parts ?? [];
        var stillNumbers = stillListed.Select(p => p.PartNumber ?? 0).ToHashSet();
        Ensure(stillNumbers.SetEquals([1, 2]), $"expected parts {{1, 2}} still present after rejections, got {Py.Repr(stillNumbers)}");

        // 3) A correctly ascending, matching part list succeeds and assembles the object.
        var result = await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket,
            Key = key,
            UploadId = uploadId,
            PartETags = [S3Fidelity.Part(1, etag1), S3Fidelity.Part(2, etag2)],
        });
        Ensure(result.Key == key, $"expected completed object Key {Py.Repr(key)}, got {Py.Repr(result.Key)}");
        var body = await S3Fidelity.GetBytes(s3, bucket, key);
        Ensure(body.Length == part1Body.Length + part2Body.Length, $"unexpected assembled size {body.Length}");
        Ensure(body.AsSpan(0, part1Body.Length).SequenceEqual(part1Body), "part 1 bytes not first in assembled object");
        Ensure(body.AsSpan(part1Body.Length).SequenceEqual(part2Body), "part 2 bytes not appended in order");
    }
}

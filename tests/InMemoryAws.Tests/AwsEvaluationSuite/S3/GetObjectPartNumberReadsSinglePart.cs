// Translated from behavioral/s3/check_get_object_part_number_reads_single_part.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.get-object-part-number-reads-single-part</summary>
[Category("AwsEvaluationSuite")]
public sealed class GetObjectPartNumberReadsSinglePart
{
    /// <summary>5 MiB — the minimum size for a non-final part.</summary>
    private const int Part1Size = 5 * 1024 * 1024;

    /// <summary>1 MiB — final part, may be smaller.</summary>
    private const int Part2Size = 1 * 1024 * 1024;

    private const int Total = Part1Size + Part2Size;

    /// <summary>
    /// HeadObject/GetObject with PartNumber on a two-part object return exactly that part (206, exact
    /// Content-Range) with PartsCount=2, a nonexistent part fails with 416 InvalidPartNumber and its
    /// exact message, and a plain GET returns the whole object without PartsCount.
    /// </summary>
    [Test]
    [DisplayName("s3.get-object-part-number-reads-single-part")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        const string key = "parted.bin";
        var part1 = S3Fidelity.Repeat('1', Part1Size);
        var part2 = S3Fidelity.Repeat('2', Part2Size);

        var uploadId = (await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = key })).UploadId;
        // Registered at creation so an upload left open by a mid-check failure is not leaked; a
        // no-op once the upload completes below.
        ctx.OnCleanup(S3Fidelity.AbortQuietly(s3, bucket, key, uploadId));
        var parts = new List<PartETag>();
        foreach (var (number, chunk) in new[] { (1, part1), (2, part2) })
        {
            var etag = (await s3.UploadPartAsync(new UploadPartRequest { BucketName = bucket, Key = key, UploadId = uploadId, PartNumber = number, InputStream = new MemoryStream(chunk) })).ETag;
            parts.Add(S3Fidelity.Part(number, etag));
        }

        await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest { BucketName = bucket, Key = key, UploadId = uploadId, PartETags = parts });

        // --- Control: a plain GET returns the whole object and reports no part framing.
        using (var whole = await s3.GetObjectAsync(bucket, key))
        {
            Ensure(whole.ContentLength == Total, $"the completed object must be {Total} bytes, got {whole.ContentLength}");
            Ensure((await S3Fidelity.ReadBody(whole)).AsSpan().SequenceEqual([.. part1, .. part2]), "the reassembled object body is wrong");
            Ensure(whole.PartsCount is null, $"a whole-object GET must not report PartsCount, got {Py.Repr(whole.PartsCount)}");
        }

        // --- HEAD PartNumber=1 is the part-discovery call: it must report the part count and the
        // FIRST part's length, not the object's length.
        var head1 = await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = key, PartNumber = 1 });
        Ensure(
            head1.PartsCount == 2,
            $"HEAD with PartNumber=1 must report PartsCount=2, got {Py.Repr(head1.PartsCount)} — the part boundaries were not retained through CompleteMultipartUpload");
        Ensure(
            head1.ContentLength == Part1Size,
            $"HEAD with PartNumber=1 must report the part's size {Part1Size}, not the object's {Total}; got {head1.ContentLength}");
        var head1Range = head1.ContentRange;
        Ensure(
            head1Range == $"bytes 0-{Part1Size - 1}/{Total}",
            $"expected ContentRange 'bytes 0-{Part1Size - 1}/{Total}', got {Py.Repr(head1Range)}");

        // --- GET PartNumber=2 must return exactly the final part's bytes at the right offset.
        using (var got2 = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key, PartNumber = 2 }))
        {
            Ensure(got2.HttpStatusCode == HttpStatusCode.PartialContent, $"a part-scoped GET is a partial response (206), got {(int)got2.HttpStatusCode}");
            Ensure(got2.PartsCount == 2, $"expected PartsCount=2, got {Py.Repr(got2.PartsCount)}");
            Ensure(got2.ContentLength == Part2Size, $"expected the final part's size {Part2Size}, got {got2.ContentLength}");
            Ensure(
                got2.ContentRange == $"bytes {Part1Size}-{Total - 1}/{Total}",
                $"expected ContentRange 'bytes {Part1Size}-{Total - 1}/{Total}', got {Py.Repr(got2.ContentRange)}");
            var body2 = await S3Fidelity.ReadBody(got2);
            Ensure(
                body2.AsSpan().SequenceEqual(part2),
                $"part 2 must be the {Part2Size} bytes uploaded as part 2; got {body2.Length} bytes starting {Py.Repr(body2.Take(8).ToArray())}");
        }

        // --- Break exactly one thing: a part number past the part count.
        var exc = await FidelityContext.CaptureError(() => s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key, PartNumber = 3 }));
        Ensure(
            exc is not null,
            "GET with PartNumber=3 on a 2-part object succeeded; PartNumber is being ignored rather than validated, so parallel-download clients get the whole object per request");
        Ensure(exc.Code == "InvalidPartNumber", $"expected InvalidPartNumber for a part number past PartsCount, got {Py.Repr(exc.Code)}");
        Ensure((int)exc.StatusCode == 416, $"expected HTTP 416 for InvalidPartNumber, got {(int)exc.StatusCode}");
        Ensure(exc.Message == "The requested partnumber is not satisfiable", exc.Message ?? "None");

        // --- Effect absent / control still works: the rejection left the object intact.
        using (var again = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key, PartNumber = 1 }))
        {
            Ensure((await S3Fidelity.ReadBody(again)).AsSpan().SequenceEqual(part1), "part 1 is no longer readable after the rejection");
        }

        var wholeLen = (await s3.GetObjectMetadataAsync(bucket, key)).ContentLength;
        Ensure(wholeLen == Total, $"the object's total length must still be {Total} after the rejected PartNumber=3 request, got {wholeLen}");
    }
}

// Translated from behavioral/s3/check_list_parts_max_parts_pagination_returns_each_part_once.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.list-parts-max-parts-pagination-returns-each-part-once</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListPartsMaxPartsPaginationReturnsEachPartOnce
{
    private const string Check = "list-parts-max-parts-pagination-returns-each-part-once";

    /// <summary>S3 multipart minimum part size (except the last part).</summary>
    private const int PartSize = 5 * 1024 * 1024;
    private const int NParts = 5;
    private const int MaxParts = 2;

    /// <summary>
    /// ListParts with MaxParts=2 over 5 uploaded parts pages with IsTruncated and
    /// NextPartNumberMarker; following PartNumberMarker returns every part exactly once, in order.
    /// </summary>
    [Test]
    [DisplayName("s3.list-parts-max-parts-pagination-returns-each-part-once")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateTaggedBucket(ctx, "sim2real", Check);
        const string key = "list-parts-pagination.bin";
        var uploadId = (await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = key })).UploadId;
        ctx.OnCleanup(S3Fidelity.AbortQuietly(s3, bucket, key, uploadId));

        var uploaded = new Dictionary<int, string>();
        for (var n = 1; n <= NParts; n++)
        {
            var body = new byte[PartSize];
            Array.Fill(body, (byte)n);
            var part = await s3.UploadPartAsync(new UploadPartRequest { BucketName = bucket, Key = key, PartNumber = n, UploadId = uploadId, InputStream = new MemoryStream(body) });
            uploaded[n] = part.ETag;
        }

        var seen = new List<int>();
        int? marker = null;
        var truncated = true;
        var pages = 0;
        while (truncated)
        {
            var request = new ListPartsRequest { BucketName = bucket, Key = key, UploadId = uploadId, MaxParts = MaxParts };
            if (marker is not null)
            {
                request.PartNumberMarker = marker.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            var resp = await s3.ListPartsAsync(request);
            pages++;
            Ensure(pages <= NParts, $"pagination did not converge after {pages} pages: seen {Py.Repr(seen)}");
            var pageNumbers = (resp.Parts ?? []).Select(p => p.PartNumber ?? 0).ToList();
            Ensure(pageNumbers.Count <= MaxParts, $"page returned {pageNumbers.Count} parts, more than MaxParts={MaxParts}: {Py.Repr(pageNumbers)}");
            foreach (var p in resp.Parts ?? [])
            {
                Ensure(
                    p.ETag == uploaded[p.PartNumber ?? 0],
                    $"ListParts ETag {Py.Repr(p.ETag)} for part {p.PartNumber} != upload_part ETag {Py.Repr(uploaded[p.PartNumber ?? 0])}");
            }

            seen.AddRange(pageNumbers);
            truncated = resp.IsTruncated == true;
            if (truncated)
            {
                var nextMarker = resp.NextPartNumberMarker;
                Ensure(
                    nextMarker is not null && nextMarker != marker,
                    $"IsTruncated=True but NextPartNumberMarker did not advance past {Py.Repr(marker)}");
                marker = nextMarker;
            }
        }

        Ensure(
            seen.SequenceEqual(Enumerable.Range(1, NParts)),
            $"paginating ListParts with MaxParts={MaxParts} must return each part exactly once, in ascending order; got {Py.Repr(seen)}");
    }
}

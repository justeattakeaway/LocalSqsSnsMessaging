// Translated from behavioral/s3/check_get_object_attributes_object_parts_max_parts_pagination_returns_each_part_once.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.get-object-attributes-object-parts-max-parts-pagination-returns-each-part-once</summary>
[Category("AwsEvaluationSuite")]
public sealed class GetObjectAttributesObjectPartsMaxPartsPaginationReturnsEachPartOnce
{
    private const string Check = "get-object-attributes-object-parts-max-parts-pagination-returns-each-part-once";

    /// <summary>S3 multipart minimum part size (except the last part).</summary>
    private const int PartSize = 5 * 1024 * 1024;
    private const int NParts = 3;
    private const int MaxParts = 2;

    /// <summary>
    /// GetObjectAttributes with ObjectAttributes=['ObjectParts'] and MaxParts=2 over 3 uploaded
    /// parts pages with IsTruncated and NextPartNumberMarker; following PartNumberMarker returns every
    /// part exactly once, in order, matching the sizes and CRC32 checksums ListParts reports.
    /// </summary>
    [Test]
    [DisplayName("s3.get-object-attributes-object-parts-max-parts-pagination-returns-each-part-once")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateTaggedBucket(ctx, "sim2real", Check, "sim2real-behav-goap-");
        const string key = "object-parts-pagination.bin";
        var uploadId = (await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = bucket,
            Key = key,
            ChecksumAlgorithm = ChecksumAlgorithm.CRC32,
        })).UploadId;
        ctx.OnCleanup(S3Fidelity.AbortQuietly(s3, bucket, key, uploadId));

        var uploaded = new SortedDictionary<int, (long Size, string ChecksumCRC32, string ETag)>();
        for (var n = 1; n <= NParts; n++)
        {
            var size = n < NParts ? PartSize : 4096;
            var body = new byte[size];
            Array.Fill(body, (byte)n);
            var part = await s3.UploadPartAsync(new UploadPartRequest
            {
                BucketName = bucket,
                Key = key,
                PartNumber = n,
                UploadId = uploadId,
                InputStream = new MemoryStream(body),
                ChecksumAlgorithm = ChecksumAlgorithm.CRC32,
            });
            uploaded[n] = (size, part.ChecksumCRC32, part.ETag);
        }

        var listPartsResp = await s3.ListPartsAsync(new ListPartsRequest { BucketName = bucket, Key = key, UploadId = uploadId });
        await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket,
            Key = key,
            UploadId = uploadId,
            PartETags = [.. uploaded.Select(p => new PartETag { PartNumber = p.Key, ETag = p.Value.ETag, ChecksumCRC32 = p.Value.ChecksumCRC32 })],
        });

        var seen = new List<int>();
        var sizes = new Dictionary<int, long?>();
        var checksums = new Dictionary<int, string?>();
        int? marker = null;
        var truncated = true;
        int? totalPartsCount = null;
        var pages = 0;
        while (truncated)
        {
            var resp = await s3.GetObjectAttributesAsync(new GetObjectAttributesRequest
            {
                BucketName = bucket,
                Key = key,
                ObjectAttributes = [ObjectAttributes.ObjectParts],
                MaxParts = MaxParts,
                PartNumberMarker = marker,
            });
            pages++;
            Ensure(pages <= NParts, $"pagination did not converge after {pages} pages: seen {Py.Repr(seen)}");
            var objectParts = resp.ObjectParts ?? throw Fail("GetObjectAttributes returned no ObjectParts");
            if (totalPartsCount is null)
            {
                totalPartsCount = objectParts.TotalPartsCount;
            }
            else
            {
                Ensure(
                    objectParts.TotalPartsCount == totalPartsCount,
                    $"TotalPartsCount changed across pages: {Py.Repr(totalPartsCount)} then {Py.Repr(objectParts.TotalPartsCount)}");
            }

            var pageNumbers = (objectParts.Parts ?? []).Select(p => p.PartNumber ?? 0).ToList();
            Ensure(pageNumbers.Count <= MaxParts, $"page returned {pageNumbers.Count} parts, more than MaxParts={MaxParts}: {Py.Repr(pageNumbers)}");
            foreach (var p in objectParts.Parts ?? [])
            {
                sizes[p.PartNumber ?? 0] = p.Size;
                checksums[p.PartNumber ?? 0] = p.ChecksumCRC32;
            }

            seen.AddRange(pageNumbers);
            truncated = objectParts.IsTruncated == true;
            if (truncated)
            {
                var nextMarker = objectParts.NextPartNumberMarker;
                Ensure(
                    nextMarker is not null && nextMarker != marker,
                    $"IsTruncated=True but NextPartNumberMarker did not advance past {Py.Repr(marker)}");
                marker = nextMarker;
            }
        }

        Ensure(
            seen.SequenceEqual(Enumerable.Range(1, NParts)),
            $"paginating GetObjectAttributes ObjectParts with MaxParts={MaxParts} must return each part exactly once, in ascending order; got {Py.Repr(seen)}");
        Ensure(totalPartsCount == NParts, $"TotalPartsCount must equal the {NParts} uploaded parts, got {Py.Repr(totalPartsCount)}");
        Ensure(
            (listPartsResp.Parts ?? []).Count == NParts,
            $"ListParts must report {NParts} parts for the completed object, got {(listPartsResp.Parts ?? []).Count}");
        foreach (var (n, expect) in uploaded)
        {
            Ensure(sizes[n] == expect.Size, $"part {n}'s GetObjectAttributes Size {Py.Repr(sizes[n])} != the {expect.Size} byte(s) uploaded");
            Ensure(
                checksums[n] == expect.ChecksumCRC32,
                $"part {n}'s GetObjectAttributes ChecksumCRC32 {Py.Repr(checksums[n])} != UploadPart's own {Py.Repr(expect.ChecksumCRC32)}");
        }
    }
}

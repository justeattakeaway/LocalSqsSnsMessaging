// Translated from behavioral/s3/check_list_multipart_uploads_paginates_and_groups_common_prefixes.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.list-multipart-uploads-paginates-and-groups-common-prefixes</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListMultipartUploadsPaginatesAndGroupsCommonPrefixes
{
    private const string Check = "list-multipart-uploads-paginates-and-groups-common-prefixes";
    private const int MaxUploads = 2;
    private static readonly string[] FlatKeys = ["flat1", "flat2", "flat3", "flat4", "flat5"];
    private static readonly string[] DirKeys = ["dirA/k1", "dirB/k2"];

    /// <summary>
    /// ListMultipartUploads with Delimiter groups uploads under a common prefix into CommonPrefixes and
    /// lists only the flat keys; with MaxUploads and no delimiter, KeyMarker/UploadIdMarker
    /// continuation returns every in-progress upload exactly once, in Key order.
    /// </summary>
    [Test]
    [DisplayName("s3.list-multipart-uploads-paginates-and-groups-common-prefixes")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateTaggedBucket(ctx, "sim2real", Check);

        foreach (var key in FlatKeys.Concat(DirKeys).Order(StringComparer.Ordinal))
        {
            var uid = (await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = key })).UploadId;
            ctx.OnCleanup(S3Fidelity.AbortQuietly(s3, bucket, key, uid));
        }

        var delim = await s3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = bucket, Delimiter = "/", MaxUploads = 10 });
        Ensure(delim.IsTruncated == false, $"expected a single page, got IsTruncated={Py.Repr(delim.IsTruncated)}");
        var listedFlat = (delim.MultipartUploads ?? []).Select(u => u.Key).Order(StringComparer.Ordinal).ToList();
        Ensure(
            listedFlat.SequenceEqual(FlatKeys.Order(StringComparer.Ordinal)),
            $"Delimiter='/' must list only the flat keys as Uploads; got {Py.Repr(listedFlat)}");
        var prefixes = (delim.CommonPrefixes ?? []).Order(StringComparer.Ordinal).ToList();
        Ensure(
            prefixes.SequenceEqual(["dirA/", "dirB/"]),
            $"Delimiter='/' must group the nested keys into CommonPrefixes; got {Py.Repr(prefixes)}");

        var allKeys = FlatKeys.Concat(DirKeys).Order(StringComparer.Ordinal).ToList();
        var seen = new List<string>();
        string? keyMarker = null;
        string? uploadIdMarker = null;
        var pages = 0;
        var truncated = true;
        while (truncated)
        {
            var request = new ListMultipartUploadsRequest { BucketName = bucket, MaxUploads = MaxUploads };
            if (keyMarker is not null)
            {
                request.KeyMarker = keyMarker;
                request.UploadIdMarker = uploadIdMarker;
            }

            var resp = await s3.ListMultipartUploadsAsync(request);
            pages++;
            Ensure(pages <= allKeys.Count, $"pagination did not converge after {pages} pages: seen {Py.Repr(seen)}");
            var pageKeys = (resp.MultipartUploads ?? []).Select(u => u.Key).ToList();
            Ensure(pageKeys.Count <= MaxUploads, $"page returned {pageKeys.Count} uploads, more than MaxUploads={MaxUploads}: {Py.Repr(pageKeys)}");
            seen.AddRange(pageKeys);
            truncated = resp.IsTruncated == true;
            if (truncated)
            {
                var nextKeyMarker = resp.NextKeyMarker;
                var nextUploadIdMarker = resp.NextUploadIdMarker;
                Ensure(!string.IsNullOrEmpty(nextKeyMarker), "IsTruncated=True but NextKeyMarker is empty");
                (keyMarker, uploadIdMarker) = (nextKeyMarker, nextUploadIdMarker);
            }
        }

        Ensure(
            seen.SequenceEqual(allKeys),
            $"paginating ListMultipartUploads with MaxUploads={MaxUploads} and KeyMarker/UploadIdMarker continuation must return every upload exactly once, in Key order; got {Py.Repr(seen)}");
    }
}

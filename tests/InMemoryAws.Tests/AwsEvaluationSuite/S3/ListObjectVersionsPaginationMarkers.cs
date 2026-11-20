// Translated from behavioral/s3/check_list_object_versions_pagination_markers.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.list-object-versions-pagination-markers</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListObjectVersionsPaginationMarkers
{
    /// <summary>
    /// ListObjectVersions paginates via NextKeyMarker/NextVersionIdMarker and enumerates every
    /// version exactly once.
    /// </summary>
    [Test]
    [DisplayName("s3.list-object-versions-pagination-markers")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var name = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-s3fid-");
        await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest { BucketName = name, VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled } });
        var expected = new HashSet<(string, string)>();
        foreach (var key in new[] { "mv/a.txt", "mv/b.txt" })
        {
            for (var i = 0; i < 2; i++)
            {
                var vid = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = name, Key = key, ContentBody = "x" })).VersionId;
                expected.Add((key, vid));
            }
        }

        var seen = new List<(string, string)>();
        var keyMarker = string.Empty;
        var versionMarker = string.Empty;
        var pages = 0;
        while (true)
        {
            var request = new ListVersionsRequest { BucketName = name, Prefix = "mv/", MaxKeys = 1 };
            if (keyMarker.Length > 0)
            {
                request.KeyMarker = keyMarker;
            }

            if (versionMarker.Length > 0)
            {
                request.VersionIdMarker = versionMarker;
            }

            var page = await s3.ListVersionsAsync(request);
            pages++;
            var pageVersions = (page.Versions ?? []).Where(v => v.IsDeleteMarker != true).ToList();
            Ensure(pageVersions.Count <= 1, $"MaxKeys=1 was not honored: page {pages} returned {pageVersions.Count} versions");
            seen.AddRange(pageVersions.Select(v => (v.Key, v.VersionId)));
            if (page.IsTruncated != true)
            {
                break;
            }

            keyMarker = page.NextKeyMarker;
            versionMarker = page.NextVersionIdMarker ?? string.Empty;
            Ensure(!string.IsNullOrEmpty(keyMarker), "IsTruncated=True must be paired with NextKeyMarker");
            Ensure(pages < 20, "version pagination did not terminate");
        }

        Ensure(pages == expected.Count, $"MaxKeys=1 across {expected.Count} versions should force {expected.Count} pages, got {pages}");
        Ensure(seen.Count == seen.Distinct().Count(), $"pagination returned duplicate versions: {Py.Repr(seen.Select(s => s.ToString()).ToList())}");
        Ensure(seen.ToHashSet().SetEquals(expected), $"expected {Py.Repr(expected.Select(s => s.ToString()).ToList())}, got {Py.Repr(seen.Select(s => s.ToString()).ToList())}");
    }
}

// Translated from behavioral/s3/check_list_object_versions.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.list-object-versions</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListObjectVersions
{
    /// <summary>ListObjectVersions returns all versions and delete markers, correctly identified and ordered.</summary>
    [Test]
    [DisplayName("s3.list-object-versions")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = bucket,
            VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled },
        });
        var v1 = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "lv-key", InputStream = new MemoryStream(Encoding.UTF8.GetBytes("v1")) })).VersionId;
        var v2 = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "lv-key", InputStream = new MemoryStream(Encoding.UTF8.GetBytes("v2")) })).VersionId;
        Ensure(!string.IsNullOrEmpty(v1) && !string.IsNullOrEmpty(v2) && v1 != v2, $"expected two distinct version ids, got {Py.Repr(v1)} and {Py.Repr(v2)}");
        var markerId = (await s3.DeleteObjectAsync(bucket, "lv-key")).VersionId;
        Ensure(
            !string.IsNullOrEmpty(markerId) && markerId != v1 && markerId != v2,
            $"expected a delete-marker version id distinct from the data versions, got {Py.Repr(markerId)} "
            + $"against data versions {Py.Repr(v1)}, {Py.Repr(v2)}");

        var resp = await s3.ListVersionsAsync(new ListVersionsRequest { BucketName = bucket, Prefix = "lv-key" });
        var versions = (resp.Versions ?? []).Where(v => v.IsDeleteMarker != true).ToList();
        var markers = (resp.Versions ?? []).Where(v => v.IsDeleteMarker == true).ToList();
        Ensure(versions.Count == 2, $"expected 2 versions, got {versions.Count}");
        Ensure(markers.Count == 1, $"expected 1 delete marker, got {markers.Count}");
        var byVersion = versions.Select(v => v.VersionId).ToHashSet(StringComparer.Ordinal);
        Ensure(
            byVersion.SetEquals([v1, v2]),
            $"expected ListObjectVersions' Versions to be exactly {{{Py.Repr(v1)}, {Py.Repr(v2)}}}, got "
            + $"{Py.Repr(byVersion.Order(StringComparer.Ordinal).ToList())}");
        Ensure(
            markers[0].VersionId == markerId,
            $"expected the listed delete marker's VersionId to be {Py.Repr(markerId)}, got {Py.Repr(markers[0].VersionId)}");
        Ensure(
            markers[0].IsLatest == true,
            $"the delete marker sits on top of the key and must be IsLatest, got {markers[0].IsLatest}");
        Ensure(
            !versions.Any(v => v.IsLatest == true),
            "no data version may be IsLatest once a delete marker is on top of the key");
    }
}

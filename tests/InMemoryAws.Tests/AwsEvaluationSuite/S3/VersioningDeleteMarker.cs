// Translated from behavioral/s3/check_versioning_delete_marker.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Net;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.versioning-delete-marker</summary>
[Category("AwsEvaluationSuite")]
public sealed class VersioningDeleteMarker
{
    private const string ErrNoSuchKey = "NoSuchKey";
    private const string ErrNoSuchVersion = "NoSuchVersion";
    private const string NoSuchVersionMsg = "The specified version does not exist.";

    /// <summary>
    /// A delete marker hides a key without destroying it: the data version survives, drops out of
    /// IsLatest, and a bare GET 404s until the marker itself is deleted, which restores the bytes.
    /// </summary>
    [Test]
    [DisplayName("s3.versioning-delete-marker")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest { BucketName = bucket, VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled } });

        const string key = "del-marker-key";
        var dataVersion = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "content" })).VersionId;
        Ensure(!string.IsNullOrEmpty(dataVersion) && dataVersion != "null", $"a PUT into a versioned bucket returned VersionId {Py.Repr(dataVersion)}");

        var delResp = await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key });
        Ensure(delResp.DeleteMarker == "true", $"DeleteMarker not set in response: {Py.Repr(delResp.DeleteMarker)}");
        var markerVersion = delResp.VersionId;
        Ensure(
            !string.IsNullOrEmpty(markerVersion) && markerVersion != dataVersion,
            $"delete marker VersionId {Py.Repr(markerVersion)} must exist and differ from the data version {Py.Repr(dataVersion)}");

        var hidden = AssertError(
            await S3Fidelity.ExpectError(() => s3.GetObjectAsync(bucket, key), "expected a ClientError, but the call SUCCEEDED"),
            "GetObject behind the delete marker", [ErrNoSuchKey], 404, exact: true);
        Ensure(hidden.Message == "The specified key does not exist.", $"GetObject behind the delete marker: expected the exact NoSuchKey message, got {hidden}");

        // The marker is a version, not a deletion — list_object_versions must show both: the data
        // version demoted from IsLatest, and the marker installed as IsLatest in its place.
        var (versions, markers) = await S3Fidelity.AllVersions(s3, bucket);
        var byVersion = versions.ToDictionary(v => v.VersionId, StringComparer.Ordinal);
        var byMarker = markers.ToDictionary(m => m.VersionId, StringComparer.Ordinal);
        Ensure(byVersion.ContainsKey(dataVersion), $"data version {Py.Repr(dataVersion)} missing from ListObjectVersions: {Py.Repr(byVersion.Keys.Order(StringComparer.Ordinal).ToList())}");
        Ensure(byVersion[dataVersion].IsLatest == false, $"data version is still IsLatest after the delete: {byVersion[dataVersion].IsLatest}");
        Ensure(byMarker.ContainsKey(markerVersion), $"delete marker {Py.Repr(markerVersion)} missing from ListObjectVersions: {Py.Repr(byMarker.Keys.Order(StringComparer.Ordinal).ToList())}");
        Ensure(byMarker[markerVersion].IsLatest == true, $"delete marker is not IsLatest: {byMarker[markerVersion].IsLatest}");

        // The data was hidden, not destroyed: a version-pinned read still returns the original bytes.
        using (var pinned = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key, VersionId = dataVersion }))
        {
            var pinnedBody = Encoding.UTF8.GetString(await S3Fidelity.ReadBody(pinned));
            Ensure(pinnedBody == "content", $"version-pinned read returned {Py.Repr(pinnedBody)}, want b'content' — the delete marker destroyed the underlying version instead of hiding it");
        }

        // Deleting the marker itself (not the data) restores the plain read.
        await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key, VersionId = markerVersion });
        using (var restored = await s3.GetObjectAsync(bucket, key))
        {
            Ensure(Encoding.UTF8.GetString(await S3Fidelity.ReadBody(restored)) == "content", "deleting the delete marker did not restore the bytes");
            Ensure(restored.VersionId == dataVersion, $"the restored read is version {Py.Repr(restored.VersionId)}, want the original {Py.Repr(dataVersion)}");
        }

        // Permanently deleting the data version destroys it: unreadable, unlisted, uncopyable.
        await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key, VersionId = dataVersion });
        var gone = AssertError(
            await S3Fidelity.ExpectError(() => s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key, VersionId = dataVersion }), "expected a ClientError, but the call SUCCEEDED"),
            "GetObject of the permanently deleted version", [ErrNoSuchVersion], 404, exact: true);
        Ensure(gone.Message == NoSuchVersionMsg, $"GetObject of the permanently deleted version: expected {Py.Repr(NoSuchVersionMsg)}, got {gone}");
        var page = await s3.ListVersionsAsync(new ListVersionsRequest { BucketName = bucket, Prefix = key });
        var left = (page.Versions ?? []).Where(v => v.Key == key).Select(v => v.VersionId).ToList();
        Ensure(left.Count == 0, $"ListObjectVersions still lists {left.Count} version(s)/marker(s) for the key after permanent delete");
        var uncopied = AssertError(
            await S3Fidelity.ExpectError(
                () => s3.CopyObjectAsync(new CopyObjectRequest { DestinationBucket = bucket, DestinationKey = key + "-copy", SourceBucket = bucket, SourceKey = key, SourceVersionId = dataVersion }),
                "expected a ClientError, but the call SUCCEEDED"),
            "CopyObject from the permanently deleted version", [ErrNoSuchVersion], 404);
        Ensure(uncopied.Message == NoSuchVersionMsg, $"CopyObject from the permanently deleted version: expected {Py.Repr(NoSuchVersionMsg)}, got {uncopied}");
    }

    /// <summary>
    /// <c>_assert_error</c>: grades a refusal on code, HTTP status class (the exact status when
    /// <paramref name="exact"/>) and a non-empty message.
    /// </summary>
    private static AwsError AssertError(AwsError error, string what, string[] codes, int status, bool exact = false)
    {
        var gotStatus = (int)error.StatusCode;
        Ensure(codes.Contains(error.Code), $"{what}: expected code in {Py.Repr(codes)}, got {Py.Repr(error.Code)}");
        if (exact)
        {
            Ensure(gotStatus == status, $"{what}: expected HTTP {status}, got {gotStatus}");
        }
        else
        {
            Ensure(gotStatus / 100 == status / 100, $"{what}: expected HTTP {status / 100}xx, got {gotStatus}");
        }

        Ensure(!string.IsNullOrEmpty(error.Message), $"{what}: expected a non-empty error message, got {Py.Repr(error.Message)}");
        return error;
    }
}

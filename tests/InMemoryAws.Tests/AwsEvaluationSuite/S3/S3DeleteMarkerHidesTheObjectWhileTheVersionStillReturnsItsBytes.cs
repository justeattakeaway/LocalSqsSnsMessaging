// Translated from behavioral/s3/check_s3_delete_marker_hides_the_object_while_the_version_still_returns_its_bytes.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3-delete-marker-hides-the-object-while-the-version-still-returns-its-bytes</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3DeleteMarkerHidesTheObjectWhileTheVersionStillReturnsItsBytes
{
    private static readonly byte[] Payload = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("the-bytes-a-delete-marker-must-hide-without-destroying", 8)));

    /// <summary>
    /// An unversioned delete adds a marker: GET/HEAD 404 (HEAD flags the marker), the pinned GET
    /// returns the bytes, ListObjectsV2 omits the key, ListObjectVersions shows the marker latest,
    /// GET on the marker is 405, and deleting the marker restores the bytes.
    /// </summary>
    [Test]
    [DisplayName("s3.s3-delete-marker-hides-the-object-while-the-version-still-returns-its-bytes")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        await S3Fidelity.SetVersioning(s3, bucket, VersionStatus.Enabled);
        var status = await S3Fidelity.VersioningStatus(s3, bucket);
        Ensure(status == "Enabled", $"versioning did not take effect: Status is {Py.Repr(status)}");

        const string key = "hidden-not-destroyed.bin";
        var dataVersion = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(Payload) })).VersionId;
        Ensure(!string.IsNullOrEmpty(dataVersion) && dataVersion != "null", $"a PUT into a versioned bucket returned VersionId {Py.Repr(dataVersion)}");

        // --- One mutation: an unversioned delete, which must create a marker rather than remove data.
        var deleted = await s3.DeleteObjectAsync(bucket, key);
        Ensure(
            deleted.DeleteMarker == "true",
            $"the unversioned delete on a versioned bucket reported DeleteMarker {Py.Repr(deleted.DeleteMarker)}; it must create a marker, not delete the object");
        var markerVersion = deleted.VersionId;
        Ensure(
            !string.IsNullOrEmpty(markerVersion) && markerVersion != dataVersion,
            $"the delete marker's VersionId is {Py.Repr(markerVersion)}, which must exist and must differ from the data version {Py.Repr(dataVersion)}");

        // --- Read 1: the plain GET is hidden by the marker.
        var hidden = await FidelityContext.CaptureError(() => s3.GetObjectAsync(bucket, key));
        Ensure(hidden is not null, "GetObject still returns the object after a delete marker was placed over it");
        Ensure(hidden!.Code == "NoSuchKey", $"expected NoSuchKey for a key hidden by a delete marker, got {Py.Repr(hidden.Code)}");
        Ensure(hidden.Message == "The specified key does not exist.", $"unexpected NoSuchKey message for a key hidden by a delete marker: {Py.Repr(hidden.Message)}");
        Ensure((int)hidden.StatusCode == 404, $"expected HTTP 404, got {(int)hidden.StatusCode}");

        // --- Read 1b: the plain HEAD also 404s, but S3 says WHY in a header — the response carries
        // x-amz-delete-marker: true, which is how a client distinguishes "hidden by a marker" from
        // "never existed". An emulator that models this as plain absence omits the header.
        var headHidden = await FidelityContext.CaptureError(() => s3.GetObjectMetadataAsync(bucket, key));
        Ensure(headHidden is not null, "HeadObject still returns the object hidden by a delete marker");
        Ensure((int)headHidden!.StatusCode == 404, $"expected HTTP 404 for a HEAD of a key hidden by a marker, got {(int)headHidden.StatusCode}");
        // (the .NET SDK keeps no headers of an error response; the same HEAD again, raw)
        using (var rawHead = await S3CoreFidelity.RawObjectRequest(ctx, HttpMethod.Head, bucket, key))
        {
            Ensure(
                (int)rawHead.StatusCode == 404 && S3CoreFidelity.Header(rawHead, "x-amz-delete-marker") == "true",
                $"the 404 for a key hidden by a delete marker must carry x-amz-delete-marker: true; the response headers were {rawHead.Headers}");
        }

        // --- Read 2: the version-pinned GET returns the exact bytes. The data was never destroyed.
        using (var pinned = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key, VersionId = dataVersion }))
        {
            var pinnedBody = await S3Fidelity.ReadBody(pinned);
            Ensure(
                pinnedBody.AsSpan().SequenceEqual(Payload),
                $"the version-pinned read returned {pinnedBody.Length} bytes, want {Payload.Length} — the delete marker destroyed the underlying version instead of hiding it");
            Ensure(pinned.VersionId == dataVersion, $"the version-pinned read echoed VersionId {Py.Repr(pinned.VersionId)}, want {Py.Repr(dataVersion)}");
        }

        // --- Read 3: ListObjectsV2 omits the key entirely.
        var listedKeys = await S3CoreFidelity.ListAllKeys(s3, bucket);
        Ensure(!listedKeys.Contains(key), $"ListObjectsV2 still shows the key hidden by a delete marker: {Py.Repr(listedKeys.Order(StringComparer.Ordinal).ToList())}");

        // --- Read 4: ListObjectVersions shows both, with the marker as IsLatest.
        var (versions, markers) = await S3Fidelity.AllVersions(s3, bucket);
        var byMarker = markers.ToDictionary(m => m.VersionId, StringComparer.Ordinal);
        var byVersion = versions.ToDictionary(v => v.VersionId, StringComparer.Ordinal);
        Ensure(byMarker.ContainsKey(markerVersion), $"the delete marker {Py.Repr(markerVersion)} is not in ListObjectVersions' DeleteMarkers: {Py.Repr(byMarker.Keys.Order(StringComparer.Ordinal).ToList())}");
        Ensure(byMarker[markerVersion].IsLatest == true, $"the delete marker is not IsLatest: {Py.Repr(byMarker[markerVersion].VersionId)}");
        Ensure(byVersion.ContainsKey(dataVersion), $"the data version {Py.Repr(dataVersion)} is not in ListObjectVersions' Versions: {Py.Repr(byVersion.Keys.Order(StringComparer.Ordinal).ToList())}");
        Ensure(byVersion[dataVersion].IsLatest == false, $"the hidden data version is still IsLatest: {Py.Repr(dataVersion)}");
        Ensure(byVersion[dataVersion].Size == Payload.Length, $"the hidden data version reports Size {byVersion[dataVersion].Size}, want {Payload.Length}");

        // --- Read 5: GET on the marker version is 405 MethodNotAllowed with Allow: DELETE, not
        // NotFound: the marker exists, and DELETE is the only verb it accepts.
        var onMarker = await FidelityContext.CaptureError(() => s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key, VersionId = markerVersion }));
        Ensure(onMarker is not null, "GetObject on the delete-marker version returned content; a marker has none");
        Ensure(
            onMarker!.Code == "MethodNotAllowed",
            $"expected MethodNotAllowed for a GET of the delete-marker version, got {Py.Repr(onMarker.Code)} — a marker that reports NoSuchVersion is being modelled as absence rather than as a version");
        Ensure(
            onMarker.Message == "The specified method is not allowed against this resource.",
            $"unexpected MethodNotAllowed message for a GET of the delete-marker version: {Py.Repr(onMarker.Message)}");
        Ensure((int)onMarker.StatusCode == 405, $"expected HTTP 405 for a GET of the delete-marker version, got {(int)onMarker.StatusCode}");
        using (var rawMarker = await S3CoreFidelity.RawObjectRequest(ctx, HttpMethod.Get, bucket, key, markerVersion))
        {
            Ensure(
                S3CoreFidelity.Header(rawMarker, "allow") == "DELETE",
                $"the 405 must advertise Allow: DELETE — the marker's only legal verb; headers were {rawMarker.Headers} {rawMarker.Content.Headers}");
        }

        // --- Read 6: deleting the MARKER by version id restores the key's bytes.
        await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key, VersionId = markerVersion });
        using (var restored = await s3.GetObjectAsync(bucket, key))
        {
            var restoredBody = await S3Fidelity.ReadBody(restored);
            Ensure(
                restoredBody.AsSpan().SequenceEqual(Payload),
                $"deleting the delete marker did not restore the bytes: got {restoredBody.Length} bytes, want {Payload.Length}");
            Ensure(restored.VersionId == dataVersion, $"the restored read is version {Py.Repr(restored.VersionId)}, want the original {Py.Repr(dataVersion)}");
        }

        var (afterVersions, afterMarkers) = await S3Fidelity.AllVersions(s3, bucket);
        Ensure(afterMarkers.Count == 0, $"delete markers survive after deleting the only one: {Py.Repr(afterMarkers.Select(m => m.VersionId).ToList())}");
        var afterIds = afterVersions.Select(v => v.VersionId).ToList();
        Ensure(afterIds.SequenceEqual([dataVersion]), $"ListObjectVersions now reports {Py.Repr(afterIds)}, want exactly [{Py.Repr(dataVersion)}]");
    }
}

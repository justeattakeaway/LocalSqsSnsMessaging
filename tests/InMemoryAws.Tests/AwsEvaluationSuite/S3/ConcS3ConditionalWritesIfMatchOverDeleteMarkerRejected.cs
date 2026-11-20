// Translated from behavioral/s3/check_conc_s3_conditional_writes_if_match_over_delete_marker_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Net;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.conc-s3-conditional-writes-if-match-over-delete-marker-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConcS3ConditionalWritesIfMatchOverDeleteMarkerRejected
{
    private const string NoSuchKeyMsg = "The specified key does not exist.";
    private const string MarkerDeleteMsg = "The specified key does not exist.";
    private static readonly byte[] V1 = Encoding.UTF8.GetBytes("first-version");
    private static readonly byte[] V2 = Encoding.UTF8.GetBytes("second-version-written-by-compare-and-swap-then-deleted");
    private static readonly byte[] Revive = Encoding.UTF8.GetBytes("a-stale-writer-trying-to-revive-a-deleted-key");

    /// <summary>
    /// When the current version is a delete marker, If-Match PutObject and DeleteObject naming the
    /// hidden version's ETag fail with NoSuchKey (exact messages), adding no version and no second marker;
    /// before the delete the same If-Match PutObject succeeds.
    /// </summary>
    [Test]
    [DisplayName("s3.conc-s3-conditional-writes-if-match-over-delete-marker-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateCondwBucket(ctx);
        await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest { BucketName = bucket, VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled } });
        Ensure((await s3.GetBucketVersioningAsync(new GetBucketVersioningRequest { BucketName = bucket })).VersioningConfig.Status == VersionStatus.Enabled, "versioning did not take effect on the bucket");
        const string key = "cas/deleted.txt";

        var r1 = await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(V1) });

        // --- POSITIVE CONTROL: If-Match with the current ETag succeeds and appends v2.
        var r2 = await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(V2), IfMatch = r1.ETag });
        Ensure(r2.HttpStatusCode == HttpStatusCode.OK, $"If-Match with the current ETag returned {(int)r2.HttpStatusCode}");
        Ensure((await S3Fidelity.GetBytes(s3, bucket, key)).AsSpan().SequenceEqual(V2), "the positive-control conditional write did not become the current version");

        // --- Break exactly one thing: make a delete marker the current version.
        var deleted = await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key });
        Ensure(deleted.DeleteMarker == "true", $"setup delete created no marker: {deleted.DeleteMarker}");
        var markerId = deleted.VersionId;

        var putExc = await S3Fidelity.ExpectError(
            () => s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(Revive), IfMatch = r2.ETag }),
            $"PutObject If-Match {Py.Repr(r2.ETag)} succeeded over a delete marker; a stale writer resurrected a deleted key");
        Ensure(putExc.Code == "NoSuchKey", $"expected NoSuchKey for If-Match over a marker, got {Py.Repr(putExc.Code)}");
        Ensure(putExc.Message == NoSuchKeyMsg, $"expected message {Py.Repr(NoSuchKeyMsg)}, got {Py.Repr(putExc.Message)}");
        Ensure(putExc.StatusCode == HttpStatusCode.NotFound, $"expected HTTP 404, got {(int)putExc.StatusCode}");

        var delExc = await S3Fidelity.ExpectError(
            () => s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key, IfMatch = r2.ETag }),
            $"DeleteObject If-Match {Py.Repr(r2.ETag)} succeeded over a delete marker");
        Ensure(delExc.Code == "NoSuchKey", $"expected NoSuchKey for a conditional delete over a marker, got {Py.Repr(delExc.Code)}");
        Ensure(delExc.Message == MarkerDeleteMsg, $"expected message {Py.Repr(MarkerDeleteMsg)} for the conditional delete, got {Py.Repr(delExc.Message)}");

        // --- Effect absent: exactly v1, v2 and the one marker; the marker is still latest.
        var (versions, markers) = await S3Fidelity.AllVersions(s3, bucket);
        var ids = versions.Select(v => v.VersionId).Order(StringComparer.Ordinal).ToList();
        Ensure(ids.SequenceEqual(new[] { r1.VersionId, r2.VersionId }.Order(StringComparer.Ordinal)), $"ListObjectVersions reports versions {Py.Repr(ids)}; want exactly v1 and v2 (no revived write)");
        var markerList = markers.Select(m => (m.VersionId, m.IsLatest == true)).ToList();
        Ensure(
            markerList.SequenceEqual([(markerId, true)]),
            $"delete markers are {Py.Repr(markerList.Select(m => m.ToString()).ToList())}; want only [({Py.Repr(markerId)}, True)]: the conditional delete must not stack a second marker");
        var hidden = await S3Fidelity.ExpectError(() => s3.GetObjectAsync(bucket, key), "the key is readable again after refused writes");
        Ensure(hidden.Code == "NoSuchKey", $"GetObject behind the marker returned {hidden}; want NoSuchKey");
        Ensure(hidden.Message == "The specified key does not exist.", $"expected the exact NoSuchKey message AWS returns, got {Py.Repr(hidden.Message)}");
        Ensure(hidden.Message == NoSuchKeyMsg, $"expected message {Py.Repr(NoSuchKeyMsg)} from GetObject, got {Py.Repr(hidden.Message)}");
        using var kept = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key, VersionId = r2.VersionId });
        var keptBody = await S3Fidelity.ReadBody(kept);
        Ensure(keptBody.AsSpan().SequenceEqual(V2), $"v2 behind the marker now holds {Py.Repr(keptBody)}; want {Py.Repr(V2)}");
    }
}

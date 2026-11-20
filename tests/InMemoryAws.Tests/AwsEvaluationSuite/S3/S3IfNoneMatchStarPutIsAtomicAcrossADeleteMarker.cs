// Translated from behavioral/s3/check_s3_if_none_match_star_put_is_atomic_across_a_delete_marker.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Net;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3-if-none-match-star-put-is-atomic-across-a-delete-marker</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3IfNoneMatchStarPutIsAtomicAcrossADeleteMarker
{
    private static readonly byte[] Original = Encoding.ASCII.GetBytes("the-original-version-hidden-behind-the-delete-marker");
    private static readonly byte[] PayloadA = Encoding.ASCII.GetBytes("payload-A-written-under-if-none-match-star");
    private static readonly byte[] PayloadB = Encoding.ASCII.GetBytes("payload-B-which-must-never-reach-the-key");

    /// <summary>
    /// If-None-Match: * checks only the current version: a PUT over a delete marker succeeds, while
    /// the same PUT over the new live object is refused, adds no version, and leaves its bytes intact.
    /// </summary>
    [Test]
    [DisplayName("s3.s3-if-none-match-star-put-is-atomic-across-a-delete-marker")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        await S3Fidelity.SetVersioning(s3, bucket, VersionStatus.Enabled);
        Ensure(await S3Fidelity.VersioningStatus(s3, bucket) == "Enabled", "versioning did not take effect on the bucket");

        const string key = "conditional-over-marker.bin";
        var originalVersion = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(Original) })).VersionId;
        var deleted = await s3.DeleteObjectAsync(bucket, key);
        var markerVersion = deleted.VersionId;
        Ensure(deleted.DeleteMarker == "true", $"the setup delete did not create a marker: DeleteMarker={Py.Repr(deleted.DeleteMarker)}, VersionId={Py.Repr(deleted.VersionId)}");

        // --- POSITIVE CONTROL: the conditional PUT must SUCCEED over a delete marker, because the
        // marker means the key has no current object even though it has two versions of history.
        var first = await Put(s3, bucket, key, PayloadA);
        Ensure(
            first.HttpStatusCode == HttpStatusCode.OK,
            $"If-None-Match:* over a delete marker was refused with {(int)first.HttpStatusCode} — the version HISTORY is being treated as object existence, so no client can ever create-if-absent a previously deleted key");
        var writtenVersion = first.VersionId;
        Ensure(
            writtenVersion != markerVersion && writtenVersion != originalVersion,
            $"the conditional PUT reported VersionId {Py.Repr(writtenVersion)}, which collides with the delete marker {Py.Repr(markerVersion)} or the original {Py.Repr(originalVersion)}");

        // --- Break exactly one thing: now the key HAS a current object, so the identical call must
        // be refused.
        var exc = await FidelityContext.CaptureError(() => Put(s3, bucket, key, PayloadB));
        Ensure(
            exc is not null,
            "the second identical If-None-Match:* PUT succeeded over an existing object — the condition is being ignored, so two concurrent create-if-absent writers both win and the loser's data is silently discarded");
        Ensure(exc!.Code == "PreconditionFailed", $"expected PreconditionFailed for If-None-Match:* over an existing object, got {Py.Repr(exc.Code)}");
        Ensure(
            exc.Message == "At least one of the pre-conditions you specified did not hold",
            $"unexpected PreconditionFailed message for If-None-Match:* over an existing object: {Py.Repr(exc.Message)}");
        Ensure((int)exc.StatusCode == 412, $"expected HTTP 412 for PreconditionFailed, got {(int)exc.StatusCode}");

        // --- Effect absent: payload B never landed, and no version was created for it.
        using (var got = await s3.GetObjectAsync(bucket, key))
        {
            var body = await S3Fidelity.ReadBody(got);
            Ensure(
                body.AsSpan().SequenceEqual(PayloadA),
                $"the refused conditional PUT overwrote the object: it holds {Py.Repr(Encoding.ASCII.GetString(body))}, want {Py.Repr(Encoding.ASCII.GetString(PayloadA))}");
            Ensure(got.VersionId == writtenVersion, $"the current version is {Py.Repr(got.VersionId)}, want the first PUT's {Py.Repr(writtenVersion)}");
        }

        var (versions, markers) = await S3Fidelity.AllVersions(s3, bucket);
        var versionIds = versions.Select(v => v.VersionId).Order(StringComparer.Ordinal).ToList();
        var want = new[] { originalVersion, writtenVersion }.Order(StringComparer.Ordinal).ToList();
        Ensure(
            versionIds.SequenceEqual(want),
            $"ListObjectVersions reports versions {Py.Repr(versionIds)}; want exactly the original and the one conditional write, {Py.Repr(want)} — the refused PUT created a version");
        var markerIds = markers.Select(m => m.VersionId).ToList();
        Ensure(markerIds.SequenceEqual([markerVersion]), $"ListObjectVersions reports delete markers {Py.Repr(markerIds)}, want exactly [{Py.Repr(markerVersion)}]");
        var latest = versions.Where(v => v.IsLatest == true).Select(v => v.VersionId).ToList();
        Ensure(latest.Distinct().SequenceEqual([writtenVersion]), $"the latest version is {Py.Repr(latest)}, want [{Py.Repr(writtenVersion)}]");

        // --- And the original version behind the marker is still readable, untouched by any of it.
        using var still = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key, VersionId = originalVersion });
        Ensure((await S3Fidelity.ReadBody(still)).AsSpan().SequenceEqual(Original), "the version hidden behind the delete marker no longer holds its original bytes");
    }

    private static Task<PutObjectResponse> Put(IAmazonS3 s3, string bucket, string key, byte[] body) =>
        s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(body), IfNoneMatch = "*" });
}

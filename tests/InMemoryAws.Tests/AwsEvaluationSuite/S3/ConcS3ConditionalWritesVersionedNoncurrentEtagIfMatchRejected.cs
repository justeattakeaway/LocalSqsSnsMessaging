// Translated from behavioral/s3/check_conc_s3_conditional_writes_versioned_noncurrent_etag_if_match_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Net;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.conc-s3-conditional-writes-versioned-noncurrent-etag-if-match-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConcS3ConditionalWritesVersionedNoncurrentEtagIfMatchRejected
{
    private const string PreconditionMsg = "At least one of the pre-conditions you specified did not hold";
    private static readonly byte[] V1 = Encoding.UTF8.GetBytes("version-one-now-noncurrent-but-still-stored");
    private static readonly byte[] V2 = Encoding.UTF8.GetBytes("version-two-the-current-version");
    private static readonly byte[] V3 = Encoding.UTF8.GetBytes("version-three-written-by-a-correct-compare-and-swap");
    private static readonly byte[] Stale = Encoding.UTF8.GetBytes("stale-writer-payload-that-must-never-become-a-version");

    /// <summary>
    /// On a versioned bucket, PutObject If-Match naming a noncurrent version's ETag is refused with 412
    /// PreconditionFailed and its exact message and adds no version; naming the current ETag appends a new
    /// current version while the older versions keep their bytes.
    /// </summary>
    [Test]
    [DisplayName("s3.conc-s3-conditional-writes-versioned-noncurrent-etag-if-match-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateCondwBucket(ctx);
        await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest { BucketName = bucket, VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled } });
        Ensure((await s3.GetBucketVersioningAsync(new GetBucketVersioningRequest { BucketName = bucket })).VersioningConfig.Status == VersionStatus.Enabled, "versioning did not take effect on the bucket");
        const string key = "cas/versioned.txt";

        var r1 = await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(V1) });
        var r2 = await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(V2) });
        Ensure(
            r1.ETag != r2.ETag && r1.VersionId != r2.VersionId,
            $"setup versions are not distinct: {Py.Repr(r1.ETag)}/{Py.Repr(r1.VersionId)} vs {Py.Repr(r2.ETag)}/{Py.Repr(r2.VersionId)}");

        // --- Break exactly one thing: condition on v1's (stored but noncurrent) ETag.
        var exc = await S3Fidelity.ExpectError(
            () => s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(Stale), IfMatch = r1.ETag }),
            $"If-Match with noncurrent v1 ETag {Py.Repr(r1.ETag)} succeeded; the precondition is being checked against version history instead of the current version");
        Ensure(exc.Code == "PreconditionFailed", $"expected PreconditionFailed, got {Py.Repr(exc.Code)}");
        Ensure(exc.Message == PreconditionMsg, $"expected message {Py.Repr(PreconditionMsg)}, got {Py.Repr(exc.Message)}");
        Ensure(exc.StatusCode == HttpStatusCode.PreconditionFailed, $"expected HTTP 412, got {(int)exc.StatusCode}");

        // --- Effect absent: exactly v1 and v2 exist, v2 is current and byte-exact.
        var (versions, markers) = await S3Fidelity.AllVersions(s3, bucket);
        var ids = versions.Select(v => v.VersionId).Order(StringComparer.Ordinal).ToList();
        Ensure(ids.SequenceEqual(new[] { r1.VersionId, r2.VersionId }.Order(StringComparer.Ordinal)), $"after the refused write ListObjectVersions reports {Py.Repr(ids)}; want exactly v1 and v2");
        Ensure(markers.Count == 0, $"the refused write created delete markers: {Py.Repr(markers.Select(m => m.VersionId).ToList())}");
        using (var current = await s3.GetObjectAsync(bucket, key))
        {
            Ensure(
                (await S3Fidelity.ReadBody(current)).AsSpan().SequenceEqual(V2) && current.VersionId == r2.VersionId,
                $"current version is {Py.Repr(current.VersionId)}; want untouched v2 {Py.Repr(r2.VersionId)}");
        }

        // --- POSITIVE CONTROL: If-Match with the CURRENT ETag appends v3.
        var r3 = await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(V3), IfMatch = r2.ETag });
        Ensure(r3.VersionId != r1.VersionId && r3.VersionId != r2.VersionId, $"the conditional write reported VersionId {Py.Repr(r3.VersionId)}, reusing an old version");
        using (var now = await s3.GetObjectAsync(bucket, key))
        {
            Ensure(
                (await S3Fidelity.ReadBody(now)).AsSpan().SequenceEqual(V3) && now.VersionId == r3.VersionId,
                $"read-after-write returned version {Py.Repr(now.VersionId)}; want new v3 {Py.Repr(r3.VersionId)}");
        }

        (versions, _) = await S3Fidelity.AllVersions(s3, bucket);
        ids = [.. versions.Select(v => v.VersionId).Order(StringComparer.Ordinal)];
        var want = new[] { r1.VersionId, r2.VersionId, r3.VersionId }.Order(StringComparer.Ordinal).ToList();
        Ensure(ids.SequenceEqual(want), $"ListObjectVersions reports {Py.Repr(ids)}; want {Py.Repr(want)}");
        var latest = versions.Where(v => v.IsLatest == true).Select(v => v.VersionId).ToList();
        Ensure(latest.SequenceEqual([r3.VersionId]), $"latest is {Py.Repr(latest)}; want [{Py.Repr(r3.VersionId)}]");
        foreach (var (rid, body) in new[] { (r1.VersionId, V1), (r2.VersionId, V2) })
        {
            using var old = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key, VersionId = rid });
            var oldBody = await S3Fidelity.ReadBody(old);
            Ensure(oldBody.AsSpan().SequenceEqual(body), $"version {Py.Repr(rid)} now holds {Py.Repr(oldBody)}; want {Py.Repr(body)}");
        }
    }
}

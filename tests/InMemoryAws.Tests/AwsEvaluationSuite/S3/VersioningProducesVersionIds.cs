// Translated from behavioral/s3/check_versioning_produces_version_ids.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.versioning-produces-version-ids</summary>
[Category("AwsEvaluationSuite")]
public sealed class VersioningProducesVersionIds
{
    /// <summary>With versioning enabled, PutObject returns distinct VersionIds that are independently
    /// resolvable to the exact bytes each put wrote.</summary>
    [Test]
    [DisplayName("s3.versioning-produces-version-ids")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        const string key = "ver-key";
        await S3Fidelity.SetVersioning(s3, bucket, VersionStatus.Enabled);
        var r1 = await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "version-1" });
        var r2 = await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "version-2" });
        var v1 = r1.VersionId;
        var v2 = r2.VersionId;
        Ensure(v1 is not null, "VersionId not returned for first put");
        Ensure(v2 is not null, "VersionId not returned for second put");
        Ensure(v1 != v2, $"two puts returned same VersionId: {v1}");

        // Independent read at the destination: ListObjectVersions (not PutObject) must carry both
        // ids, and each id must resolve — via GetObject, a third operation — to the exact bytes that
        // put created it, ruling out a target that echoes a distinct string without storing anything.
        var listed = ((await s3.ListVersionsAsync(new ListVersionsRequest { BucketName = bucket, Prefix = key })).Versions ?? [])
            .Where(v => v.IsDeleteMarker != true).ToList();
        var listedIds = listed.Select(v => v.VersionId).ToHashSet();
        Ensure(listedIds.Contains(v1) && listedIds.Contains(v2), $"expected versions {Py.Repr(v1)} and {Py.Repr(v2)} in ListObjectVersions, got {Py.Repr(listedIds.ToList())}");
        var latest = listed.First(v => v.VersionId == v2);
        Ensure(latest.IsLatest == true, $"expected {Py.Repr(v2)} (the second put) marked IsLatest, got IsLatest={latest.IsLatest}");

        using var get1 = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key, VersionId = v1 });
        var body1 = Encoding.ASCII.GetString(await S3Fidelity.ReadBody(get1));
        using var get2 = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key, VersionId = v2 });
        var body2 = Encoding.ASCII.GetString(await S3Fidelity.ReadBody(get2));
        Ensure(body1 == "version-1", $"GetObject(VersionId={Py.Repr(v1)}) returned {Py.Repr(body1)}, expected b'version-1'");
        Ensure(body2 == "version-2", $"GetObject(VersionId={Py.Repr(v2)}) returned {Py.Repr(body2)}, expected b'version-2'");
    }
}

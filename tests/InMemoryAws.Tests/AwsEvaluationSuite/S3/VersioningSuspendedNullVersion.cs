// Translated from behavioral/s3/check_versioning_suspended_null_version.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.versioning-suspended-null-version</summary>
[Category("AwsEvaluationSuite")]
public sealed class VersioningSuspendedNullVersion
{
    /// <summary>While versioning is Suspended, PutObject writes/overwrites the single version-id 'null' object
    /// in place, and the numbered version created while versioning was Enabled survives untouched.</summary>
    [Test]
    [DisplayName("s3.versioning-suspended-null-version")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        await S3Fidelity.SetVersioning(s3, bucket, VersionStatus.Enabled);
        var r1 = await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "k", ContentBody = "enabled-version" });
        var v1 = r1.VersionId;
        Ensure(v1 is not null and not "null", $"expected a real version id for the Enabled-state write, got {Py.Repr(v1)}");

        await S3Fidelity.SetVersioning(s3, bucket, VersionStatus.Suspended);
        var status = await S3Fidelity.VersioningStatus(s3, bucket);
        Ensure(status == "Suspended", $"expected Suspended, got {Py.Repr(status)}");

        var resp = await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "k", ContentBody = "suspended-write-1" });
        Ensure(resp.VersionId is null, $"PutObject must omit VersionId for a Suspended-state write, got {Py.Repr(resp.VersionId)}");

        var versions = await Versions(s3, bucket);
        var ids = versions.Select(v => v.VersionId).ToHashSet();
        Ensure(ids.Contains(v1), $"the numbered Enabled-state version must survive a Suspended overwrite, got {Py.Repr(ids.Order(StringComparer.Ordinal).ToList())}");
        var nullVersions = versions.Count(v => v.VersionId == "null");
        Ensure(nullVersions == 1, $"expected exactly one null version after the first Suspended write, got {nullVersions}");

        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "k", ContentBody = "suspended-write-2" });
        var versions2 = await Versions(s3, bucket);
        var nullVersions2 = versions2.Count(v => v.VersionId == "null");
        Ensure(nullVersions2 == 1, $"a second Suspended write must overwrite the null version in place, not append, got {nullVersions2}");
        var ids2 = versions2.Select(v => v.VersionId).ToHashSet();
        Ensure(ids2.Contains(v1), $"the numbered Enabled-state version must still survive after the second Suspended write, got {Py.Repr(ids2.Order(StringComparer.Ordinal).ToList())}");

        var current = Encoding.ASCII.GetString(await S3Fidelity.GetBytes(s3, bucket, "k"));
        Ensure(current == "suspended-write-2", "GetObject must return the latest Suspended-state write");
    }

    private static async Task<List<S3ObjectVersion>> Versions(IAmazonS3 s3, string bucket) =>
        [.. ((await s3.ListVersionsAsync(new ListVersionsRequest { BucketName = bucket, Prefix = "k" })).Versions ?? []).Where(v => v.IsDeleteMarker != true)];
}

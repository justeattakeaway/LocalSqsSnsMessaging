// Translated from behavioral/s3/check_op_contract_list_object_versions.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.op-contract-listObjectVersions</summary>
[Category("AwsEvaluationSuite")]
public sealed class OpContractListObjectVersions
{
    private const string BucketPrefix = "sim2real-behav-";

    // S3's refusal of a bucket that does not exist, as real S3 returns it.
    private static readonly (string?, string?) NoSuchBucket = ("NoSuchBucket", "The specified bucket does not exist");

    /// <summary>ListObjectVersions reports version history and delete markers with correct IsLatest flags.</summary>
    [Test]
    [DisplayName("s3.op-contract-listObjectVersions")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var bucket = await S3Fidelity.CreateBucket(ctx);
        var s3 = ctx.S3;
        await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = bucket,
            VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled },
        });

        const string key = "versioned.txt";
        var v1 = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "one" })).VersionId;
        var v2 = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "two" })).VersionId;
        Ensure(v1 != v2, "expected two distinct version ids");

        var resp = await s3.ListVersionsAsync(new ListVersionsRequest { BucketName = bucket, Prefix = key });
        var all = resp.Versions ?? [];
        var versions = all.Where(v => v.IsDeleteMarker != true).ToDictionary(v => v.VersionId, StringComparer.Ordinal);
        var markers = all.Where(v => v.IsDeleteMarker == true).ToList();
        Ensure(versions.Keys.ToHashSet().SetEquals([v1, v2]), $"expected versions {v1},{v2} got {string.Join(", ", versions.Keys)}");
        Ensure(versions[v2].IsLatest == true, "newest put should be IsLatest");
        Ensure(versions[v1].IsLatest == false, "older version should not be IsLatest");
        Ensure(markers.Count == 0, $"expected no delete markers before any delete, got {markers.Count}");

        // deleting the key (versioned bucket) creates a delete marker instead of removing history
        var delResp = await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key });
        var markerId = delResp.VersionId;
        ctx.OnCleanup(() => s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key, VersionId = markerId }));
        ctx.OnCleanup(() => s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key, VersionId = v1 }));
        ctx.OnCleanup(() => s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key, VersionId = v2 }));

        var resp2 = await s3.ListVersionsAsync(new ListVersionsRequest { BucketName = bucket, Prefix = key });
        var all2 = resp2.Versions ?? [];
        var markers2 = all2.Where(v => v.IsDeleteMarker == true).ToList();
        Ensure(markers2.Count == 1, $"expected one delete marker, got {markers2.Count}");
        Ensure(markers2[0].IsLatest == true, $"the delete marker must be IsLatest, got {markers2[0].VersionId}");
        Ensure(markers2[0].VersionId == markerId, $"expected delete marker VersionId {markerId}, got {markers2[0].VersionId}");
        var stillVersions = all2.Where(v => v.IsDeleteMarker != true).Select(v => v.VersionId).ToHashSet();
        Ensure(stillVersions.SetEquals([v1, v2]), "prior object versions must remain listed after delete marker creation");

        // distinct error boundary: nonexistent bucket
        var missing = FidelityContext.UniqueName(BucketPrefix + "missing-");
        var err = await FidelityContext.CaptureError(() => s3.ListVersionsAsync(new ListVersionsRequest { BucketName = missing }))
            ?? throw Fail("expected a ClientError, but the call SUCCEEDED");
        Ensure((err.Code, err.Message) == NoSuchBucket, $"expected NoSuchBucket, got {err}");
    }
}

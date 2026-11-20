// Translated from behavioral/s3/check_e2e_log_rotation_pattern.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.e2e-log-rotation-pattern</summary>
[Category("AwsEvaluationSuite")]
public sealed class E2eLogRotationPattern
{
    /// <summary>Log rotation with versioning: overwrite same key 3 times, verify all versions retrievable.</summary>
    [Test]
    [DisplayName("s3.e2e-log-rotation-pattern")]
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
        const string key = "logs/app.log";
        string[] contents = ["log-entry-1: startup", "log-entry-2: running", "log-entry-3: shutdown"];
        var versionIds = new List<string>();

        // Write the same key 3 times (simulating log rotation with versioning)
        foreach (var content in contents)
        {
            var resp = await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(Encoding.UTF8.GetBytes(content)) });
            var vid = resp.VersionId;
            Ensure(vid is not null, "VersionId not returned for versioned put");
            versionIds.Add(vid);
        }

        // ListObjectVersions: verify 3 versions exist for that key
        var listing = await s3.ListVersionsAsync(new ListVersionsRequest { BucketName = bucket, Prefix = key });
        var versions = (listing.Versions ?? []).Where(v => v.IsDeleteMarker != true).ToList();
        Ensure(versions.Count == 3, $"Expected 3 versions, got {versions.Count}");

        // Exactly one version is flagged latest, and it is the most recently written one.
        var latestFlagged = versions.Where(v => v.IsLatest == true).ToList();
        Ensure(latestFlagged.Count == 1, $"Expected exactly one IsLatest=True version, got {latestFlagged.Count}");
        Ensure(
            latestFlagged[0].VersionId == versionIds[2],
            $"Expected IsLatest version {Py.Repr(versionIds[2])}, got {Py.Repr(latestFlagged[0].VersionId)}");

        // Get the earliest version (first written) and verify it has the first content
        using var first = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key, VersionId = versionIds[0] });
        var gotFirst = Encoding.UTF8.GetString(await S3Fidelity.ReadBody(first));
        Ensure(gotFirst == contents[0], $"Expected first version content {Py.Repr(contents[0])}, got {Py.Repr(gotFirst)}");

        // Default GET returns the latest version
        var gotLatest = Encoding.UTF8.GetString(await S3Fidelity.GetBytes(s3, bucket, key));
        Ensure(gotLatest == contents[2], $"Expected latest content {Py.Repr(contents[2])}, got {Py.Repr(gotLatest)}");
    }
}

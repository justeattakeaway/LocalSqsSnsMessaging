// Translated from behavioral/s3/check_deep_s3_storage_class_versioned_expiration_rule_targets_current_version.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.deep-s3-storage-class-versioned-expiration-rule-targets-current-version</summary>
/// <remarks>
/// boto3 hands the check the raw <c>x-amz-expiration</c> header as <c>Expiration</c>; the .NET SDK
/// parses it, so the port reads the raw header from the response.
/// </remarks>
[Category("AwsEvaluationSuite")]
public sealed class DeepS3StorageClassVersionedExpirationRuleTargetsCurrentVersion
{
    /// <summary>
    /// On a versioned bucket the stored Expiration rule stamps only the current in-prefix version
    /// with x-amz-expiration naming the rule, and the versionless delete that expiry performs adds a
    /// delete marker while both data versions keep their exact bytes.
    /// </summary>
    [Test]
    [DisplayName("s3.deep-s3-storage-class-versioned-expiration-rule-targets-current-version")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        const string key = "logs/app.log";
        const string outside = "other/app.log";
        const string ruleId = "expire-current-and-noncurrent";

        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-dsc-ve-");
        await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest { BucketName = bucket, VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled } });

        var v1 = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "version-one" })).VersionId;
        var v2 = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "version-two" })).VersionId;
        Ensure(!string.IsNullOrEmpty(v1) && !string.IsNullOrEmpty(v2) && v1 != v2, $"versioned PUTs minted version ids {Py.Repr(v1)} and {Py.Repr(v2)}");
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = outside, ContentBody = "not-in-scope" });

        await s3.PutLifecycleConfigurationAsync(new PutLifecycleConfigurationRequest
        {
            BucketName = bucket,
            Configuration = new LifecycleConfiguration
            {
                Rules =
                [
                    new LifecycleRule
                    {
                        Id = ruleId,
                        Status = LifecycleRuleStatus.Enabled,
                        Filter = new LifecycleFilter { Prefix = "logs/" },
                        Expiration = new LifecycleRuleExpiration { Days = 3 },
                        NoncurrentVersionExpiration = new LifecycleRuleNoncurrentVersionExpiration { NoncurrentDays = 1, NewerNoncurrentVersions = 1 },
                    },
                ],
            },
        });
        var (stored, storedCode) = await S3Fidelity.Poll(ctx, () => S3Fidelity.LifecycleRules(s3, bucket), got => got.Rules is not null);
        var rules = stored ?? [];
        Ensure(rules.Count == 1, $"lifecycle read back as {Py.Repr((object?)stored ?? storedCode)}");
        Ensure(
            Py.Equal(rules[0].GetValueOrDefault("Expiration"), new Dictionary<string, object?> { ["Days"] = 3 }),
            $"stored Expiration {Py.Repr(rules[0].GetValueOrDefault("Expiration"))}");
        Ensure(
            Py.Equal(rules[0].GetValueOrDefault("NoncurrentVersionExpiration"), new Dictionary<string, object?> { ["NoncurrentDays"] = 1, ["NewerNoncurrentVersions"] = 1 }),
            $"stored NoncurrentVersionExpiration {Py.Repr(rules[0].GetValueOrDefault("NoncurrentVersionExpiration"))}");

        var versions = await Versions(s3, bucket, "logs/");
        Ensure(versions.Markers.Count == 0, $"unexpected delete markers before expiry: {Py.Repr(versions.Markers)}");
        Ensure(
            Py.Equal(versions.Versions, new Dictionary<string, object?> { [v1] = false, [v2] = true }),
            $"version structure under logs/ was {Py.Repr(versions.Versions)}; expected v1 noncurrent, v2 current");

        // The current version inside the prefix is stamped with this rule's expiration.
        var expiration = await S3Fidelity.Poll(
            ctx,
            async () => (await S3Fidelity.WithHeaders(s3, () => s3.GetObjectMetadataAsync(bucket, key))).Headers.GetValueOrDefault("x-amz-expiration"),
            value => !string.IsNullOrEmpty(value));
        Ensure(
            !string.IsNullOrEmpty(expiration) && expiration.Contains($"rule-id=\"{ruleId}\"", StringComparison.Ordinal),
            $"current version x-amz-expiration was {Py.Repr(expiration)}, expected it to name rule-id {Py.Repr(ruleId)}");
        var expiry = S3Fidelity.ExpiryDate(expiration);
        var head = await s3.GetObjectMetadataAsync(bucket, key);
        var lastModified = S3Fidelity.Utc(head.LastModified);
        Ensure(
            expiry > lastModified,
            $"expiry-date {Py.Repr(expiration.Split("expiry-date=\"", 2)[1].Split('"', 2)[0])} is not after the object's LastModified {lastModified:O}");
        var (_, other) = await S3Fidelity.WithHeaders(s3, () => s3.GetObjectMetadataAsync(bucket, outside));
        Ensure(!other.ContainsKey("x-amz-expiration"), $"object outside the rule's prefix was stamped {Py.Repr(other.GetValueOrDefault("x-amz-expiration"))}");

        // What the Expiration action does to a versioned object: a versionless delete.
        var deleted = await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key });
        Ensure(deleted.DeleteMarker == "true", $"versionless delete did not report a delete marker: {Py.Repr(deleted.DeleteMarker)}");
        var markerId = deleted.VersionId;

        var after = await Versions(s3, bucket, "logs/");
        Ensure(
            after.Markers.Count == 1 && after.Markers[0] == (markerId, true),
            $"delete markers after expiry act: {Py.Repr(after.Markers)}");
        Ensure(
            Py.Equal(after.Versions, new Dictionary<string, object?> { [v1] = false, [v2] = false }),
            $"data versions after expiry act: {Py.Repr(after.Versions)}; both must survive as noncurrent");
        foreach (var (vid, body) in new[] { (v1, "version-one"), (v2, "version-two") })
        {
            using var got = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key, VersionId = vid });
            var data = System.Text.Encoding.UTF8.GetString(await S3Fidelity.ReadBody(got));
            Ensure(data == body, $"version {Py.Repr(vid)} returned {Py.Repr(data)} after the delete marker, expected {Py.Repr(body)}");
        }

        var (hidden, _) = await S3Fidelity.ClientError(() => s3.GetObjectMetadataAsync(bucket, key));
        Ensure(hidden == "404", $"versionless HeadObject behind the delete marker returned {Py.Repr(hidden)}, expected 404");
    }

    private static async Task<(Dictionary<string, object?> Versions, List<(string VersionId, bool IsLatest)> Markers)> Versions(IAmazonS3 s3, string bucket, string prefix)
    {
        var versions = new Dictionary<string, object?>(StringComparer.Ordinal);
        var markers = new List<(string, bool)>();
        var page = await s3.ListVersionsAsync(new ListVersionsRequest { BucketName = bucket, Prefix = prefix });
        foreach (var v in page.Versions ?? [])
        {
            if (v.IsDeleteMarker == true)
            {
                markers.Add((v.VersionId, v.IsLatest == true));
            }
            else
            {
                versions[v.VersionId] = v.IsLatest == true;
            }
        }

        return (versions, markers);
    }
}

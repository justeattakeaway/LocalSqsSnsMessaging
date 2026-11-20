// Translated from behavioral/events__s3__sqs/check_xsvc_s3_1_versioned_delete_emits_object_deleted_with_deletion_type.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.EventsS3Sqs;

/// <summary>events__s3__sqs.xsvc-s3-1-versioned-delete-emits-object-deleted-with-deletion-type</summary>
[Category("AwsEvaluationSuite")]
public sealed class XsvcS31VersionedDeleteEmitsObjectDeletedWithDeletionType
{
    private const string Check = "xsvc-s3-1-versioned-delete-emits-object-deleted-with-deletion-type";
    private const string Prefix = "sim2real-xsvc-s3d-";
    private const string Created = "Object Created";
    private const string Deleted = "Object Deleted";
    private const string Marker = "Delete Marker Created";
    private const string Permanent = "Permanently Deleted";
    private const double DeliveryBudgetS = 180.0;
    private const double VersioningWaitS = 60.0;
    private const double AbsenceFloorBudgetS = 20.0;
    private const double AbsenceCeilingBudgetS = 60.0;

    /// <summary>
    /// On a versioned bucket a plain DeleteObject emits Object Deleted with deletion-type Delete
    /// Marker Created and a versioned one Permanently Deleted, both with reason DeleteObject and the
    /// version id acted on.
    /// </summary>
    [Test]
    [DisplayName("events__s3__sqs.xsvc-s3-1-versioned-delete-emits-object-deleted-with-deletion-type")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var (ev, s3, sqs) = (ctx.Events, ctx.S3, ctx.Sqs);
        var account = ctx.Account;

        var bucket = await MakeBucket(ctx, s3);
        await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = bucket,
            VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled },
        });
        var versioning = FidelityTime.StartNew();
        string? status;
        while ((status = (await s3.GetBucketVersioningAsync(new GetBucketVersioningRequest { BucketName = bucket })).VersioningConfig.Status?.Value) != "Enabled")
        {
            Ensure(versioning.Elapsed.TotalSeconds < VersioningWaitS, $"bucket versioning never reported Enabled within {VersioningWaitS}s; last Status {Py.Repr(status)}");
            await ctx.Delay(TimeSpan.FromSeconds(2));
        }

        var (createdUrl, createdArn) = await XsvcS3Mailbox.MakeQueue(ctx, sqs, account, Prefix, Check);
        var (deletedUrl, deletedArn) = await XsvcS3Mailbox.MakeQueue(ctx, sqs, account, Prefix, Check);
        await XsvcS3Mailbox.MakeRule(ctx, ev, Pattern(Created, bucket), createdArn, Prefix, Check);
        await XsvcS3Mailbox.MakeRule(ctx, ev, Pattern(Deleted, bucket), deletedArn, Prefix, Check);

        await s3.PutBucketNotificationAsync(new PutBucketNotificationRequest { BucketName = bucket, EventBridgeConfiguration = new EventBridgeConfiguration() });
        var enabled = await s3.GetBucketNotificationAsync(bucket);
        Ensure(enabled.EventBridgeConfiguration is not null, "bucket must be wired to EventBridge; GetBucketNotificationConfiguration -> no EventBridgeConfiguration");

        var createdBox = new XsvcS3Mailbox(sqs, createdUrl, "Object Created queue");
        var deletedBox = new XsvcS3Mailbox(sqs, deletedUrl, "Object Deleted queue");
        const string key = "docs/report.txt";

        // Retried: a freshly created rule/target can silently drop its first delivered event. Each
        // retry mints a NEW version, so an unobserved attempt's version is explicitly deleted
        // (permanently, by VersionId -- no marker) before the next attempt.
        var createdWait = FidelityTime.StartNew();
        JsonObject? createdEnv = null;
        double? createdLatency = null;
        string? version1 = null;
        while (createdLatency is null)
        {
            var put = await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "sim2real-versioned-bytes" });
            var candidate = put.VersionId;
            Ensure(!string.IsNullOrEmpty(candidate) && candidate != "null", $"a versioned bucket must mint a real VersionId, got {Py.Repr(candidate)}");
            var remaining = DeliveryBudgetS - createdWait.Elapsed.TotalSeconds;
            Ensure(remaining > 0, $"{Py.Repr(Created)} for version {Py.Repr(candidate)} never delivered after retries; (detail-type, deletion-type) seen: {Py.Repr(createdBox.Seen())}");
            (createdEnv, createdLatency) = await createdBox.Poll(e => (string?)e["detail"]!["object"]!["version-id"] == candidate, Math.Min(30.0, remaining));
            if (createdEnv is null)
            {
                await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key, VersionId = candidate });
                continue;
            }

            version1 = candidate;
        }

        Ensure((string?)createdEnv!["detail-type"] == Created, $"created rule delivered {Py.Repr((string?)createdEnv["detail-type"])}");

        // --- delete 1: no VersionId -> a delete marker -----------------------------------------
        var markerWait = FidelityTime.StartNew();
        JsonObject? markerEnv = null;
        double? markerLatency = null;
        string? markerId = null;
        while (markerLatency is null)
        {
            var d1 = await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key });
            Ensure(
                d1.DeleteMarker == "true",
                $"DeleteObject without VersionId on a versioned bucket must create a marker; response DeleteMarker={Py.Repr(d1.DeleteMarker)}");
            var candidateMarker = d1.VersionId;
            var remaining = DeliveryBudgetS - markerWait.Elapsed.TotalSeconds;
            Ensure(remaining > 0, $"marker {Py.Repr(candidateMarker)} never delivered after retries; (detail-type, deletion-type) seen: {Py.Repr(deletedBox.Seen())}");
            (markerEnv, markerLatency) = await deletedBox.Poll(
                e => (string?)e["detail"]!["deletion-type"] == Marker && (string?)e["detail"]!["object"]!["version-id"] == candidateMarker,
                Math.Min(30.0, remaining));
            if (markerEnv is null)
            {
                await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key, VersionId = candidateMarker });
                continue;
            }

            markerId = candidateMarker;
        }

        Ensure((string?)markerEnv!["source"] == "aws.s3", $"source {Py.Repr((string?)markerEnv["source"])}");
        Ensure((string?)markerEnv["detail-type"] == Deleted, $"detail-type must be {Py.Repr(Deleted)}, got {Py.Repr((string?)markerEnv["detail-type"])}");
        Ensure((string?)markerEnv["detail"]!["reason"] == "DeleteObject", $"reason must be 'DeleteObject', got {Py.Repr((string?)markerEnv["detail"]!["reason"])}");
        Ensure(
            (string?)markerEnv["detail"]!["bucket"]!["name"] == bucket,
            $"detail.bucket.name {Py.Repr((string?)markerEnv["detail"]!["bucket"]!["name"])} != {Py.Repr(bucket)}");
        Ensure(
            (string?)markerEnv["detail"]!["object"]!["key"] == key,
            $"detail.object.key {Py.Repr((string?)markerEnv["detail"]!["object"]!["key"])} != {Py.Repr(key)}");
        Ensure(
            (string?)markerEnv["detail"]!["object"]!["version-id"] == markerId,
            $"the marker event must carry the marker's version id {Py.Repr(markerId)} that DeleteObject " +
            $"returned; got {Py.Repr((string?)markerEnv["detail"]!["object"]!["version-id"])}");

        // --- delete 2: explicit VersionId -> permanent ------------------------------------------
        var d2 = await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key, VersionId = version1 });
        Ensure(d2.VersionId == version1, $"DeleteObject(VersionId) must echo {Py.Repr(version1)}, got {Py.Repr(d2.VersionId)}");
        // Matched on version-id, not just deletion-type: a dropped-marker retry above cleans up its
        // stray marker by VersionId, which itself emits its OWN "Permanently Deleted" event.
        var (permEnv, permLatency) = await deletedBox.WaitFor(
            e => (string?)e["detail"]!["deletion-type"] == Permanent && (string?)e["detail"]!["object"]!["version-id"] == version1,
            DeliveryBudgetS,
            $"{Py.Repr(Deleted)} with deletion-type {Py.Repr(Permanent)} for {Py.Repr(version1)}");
        Ensure((string?)permEnv["detail-type"] == Deleted, $"detail-type must be {Py.Repr(Deleted)}, got {Py.Repr((string?)permEnv["detail-type"])}");
        Ensure((string?)permEnv["detail"]!["reason"] == "DeleteObject", $"reason must be 'DeleteObject', got {Py.Repr((string?)permEnv["detail"]!["reason"])}");
        Ensure(
            (string?)permEnv["detail"]!["object"]!["key"] == key,
            $"detail.object.key {Py.Repr((string?)permEnv["detail"]!["object"]!["key"])} != {Py.Repr(key)}");
        Ensure(
            (string?)permEnv["detail"]!["object"]!["version-id"] == version1,
            $"the permanent-delete event must carry the deleted version id {Py.Repr(version1)}; got {Py.Repr((string?)permEnv["detail"]!["object"]!["version-id"])}");

        // Independent observable at S3: the marker survives, the original version is gone.
        var listing = await s3.ListVersionsAsync(new ListVersionsRequest { BucketName = bucket, Prefix = key });
        var versions = (listing.Versions ?? []).Where(v => v.IsDeleteMarker != true).Select(v => v.VersionId).ToList();
        var markers = (listing.Versions ?? []).Where(v => v.IsDeleteMarker == true).Select(v => v.VersionId).ToList();
        Ensure(
            versions.Count == 0 && markers.SequenceEqual([markerId]),
            $"after both deletes S3 must hold only the marker {Py.Repr(markerId)}; versions={Py.Repr(versions)} markers={Py.Repr(markers)}");

        // --- routing specificity ----------------------------------------------------------------
        var window = Math.Min(
            Math.Max(AbsenceFloorBudgetS, 3.0 * Math.Max(createdLatency.Value, Math.Max(markerLatency.Value, permLatency))),
            AbsenceCeilingBudgetS);
        await createdBox.Soak(window / 2);
        await deletedBox.Soak(window / 2);
        var wrongOnDeleted = deletedBox.Envelopes().Select(e => (string)e["detail-type"]!).Distinct().Where(t => t != Deleted).Order(StringComparer.Ordinal).ToList();
        var wrongOnCreated = createdBox.Envelopes().Select(e => (string)e["detail-type"]!).Distinct().Where(t => t != Created).Order(StringComparer.Ordinal).ToList();
        Ensure(
            wrongOnDeleted.Count == 0 && wrongOnCreated.Count == 0,
            $"each rule matches one detail-type; strays on the Deleted queue {Py.Repr(wrongOnDeleted)}, on the Created queue {Py.Repr(wrongOnCreated)}");
        var kinds = deletedBox.Envelopes().Select(e => (string?)e["detail"]!["deletion-type"]).Distinct().Order(StringComparer.Ordinal).ToList();
        Ensure(kinds.SequenceEqual([Marker, Permanent]), $"two deletes must yield exactly the two deletion-types; got {Py.Repr(kinds)}");
    }

    private static JsonObject Pattern(string detailType, string bucket) => new()
    {
        ["source"] = new JsonArray("aws.s3"),
        ["detail-type"] = new JsonArray(detailType),
        ["detail"] = new JsonObject { ["bucket"] = new JsonObject { ["name"] = new JsonArray(bucket) } },
    };

    private static async Task<string> MakeBucket(FidelityContext ctx, AmazonS3Client s3)
    {
        var bucket = FidelityContext.UniqueName(Prefix);
        var request = new PutBucketRequest { BucketName = bucket, UseClientRegion = false };
        if (ctx.Region != "us-east-1")
        {
            request.PutBucketConfiguration = new PutBucketConfiguration { LocationConstraint = new BucketLocationConstraint(ctx.Region) };
        }

        await s3.PutBucketAsync(request);
        ctx.OnCleanup(() => XsvcS3Mailbox.EmptyAndDeleteBucket(s3, bucket));
        await s3.PutBucketTaggingAsync(new PutBucketTaggingRequest { BucketName = bucket, TagSet = [new Tag { Key = "sim2real", Value = Check }] });
        return bucket;
    }
}

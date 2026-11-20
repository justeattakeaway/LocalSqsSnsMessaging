// Translated from behavioral/events__s3__sqs/check_obs_s3_3_versioned_delete_marker_and_version_delete_reach_both_paths.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;
using static InMemoryAws.Tests.AwsEvaluationSuite.EventsS3Sqs.ObsS3EventPaths;

namespace InMemoryAws.Tests.AwsEvaluationSuite.EventsS3Sqs;

/// <summary>events__s3__sqs.obs-s3-3-versioned-delete-marker-and-version-delete-reach-both-paths</summary>
[Category("AwsEvaluationSuite")]
public sealed class ObsS33VersionedDeleteMarkerAndVersionDeleteReachBothPaths
{
    private const string Check = "obs-s3-3-versioned-delete-marker-and-version-delete-reach-both-paths";

    /// <summary>
    /// On a versioned bucket a versionless DeleteObject reaches both paths as a delete-marker
    /// creation naming the marker's version id, and deleting a specific version as a permanent
    /// delete naming that version.
    /// </summary>
    [Test]
    [DisplayName("events__s3__sqs.obs-s3-3-versioned-delete-marker-and-version-delete-reach-both-paths")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var paths = new ObsS3EventPaths(ctx, Check);
        var s3 = ctx.S3;
        var sqs = ctx.Sqs;
        var ev = ctx.Events;
        var account = ctx.Account;
        const string partition = "aws";

        var bucket = await paths.Bucket(s3, versioned: true);
        var status = (await s3.GetBucketVersioningAsync(new GetBucketVersioningRequest { BucketName = bucket })).VersioningConfig.Status?.Value;
        Ensure(status == "Enabled", $"bucket versioning reads back {Py.Repr(status)}");
        var (curl, carn) = await paths.Queue(sqs);
        await Allow(sqs, curl, carn, "s3.amazonaws.com", $"arn:{partition}:s3:::{bucket}", account);
        var (eurl, _) = await paths.RuleToQueue(ev, sqs, new JsonObject
        {
            ["source"] = new JsonArray("aws.s3"),
            ["detail"] = new JsonObject { ["bucket"] = new JsonObject { ["name"] = new JsonArray(bucket) } },
        }, account);
        await paths.WireBucket(s3, bucket, [(carn, ["s3:ObjectCreated:Put", "s3:ObjectRemoved:*"])]);
        var (classic, bridge) = (new Inbox(sqs, curl, "classic"), new Inbox(sqs, eurl, "eventbridge"));

        async Task<string> Fire(int i)
        {
            var k = $"warm-{i}";
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = k, ContentBody = "sim2real-warm" });
            return k;
        }

        await WarmUp(Fire, [(classic, k => Classic(k, "ObjectCreated:Put")), (bridge, k => Bridge(k, "Object Created"))]);

        var key = FidelityContext.UniqueName("versioned-");
        var put = await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "sim2real-v1" });
        var v1 = put.VersionId;
        var cPut = (await paths.Await(classic, Classic(key, "ObjectCreated:Put"), "classic Put warm-up"))[0];
        var ePut = (await paths.Await(bridge, Bridge(key, "Object Created"), "EventBridge creation warm-up"))[0];
        Ensure(
            (string?)cPut["s3"]!["object"]!["versionId"] == v1,
            $"classic Put versionId {Py.Repr((string?)cPut["s3"]!["object"]!["versionId"])} != {Py.Repr(v1)}");
        Ensure(
            (string?)ePut["detail"]!["object"]!["version-id"] == v1,
            $"EventBridge creation version-id {Py.Repr((string?)ePut["detail"]!["object"]!["version-id"])} != {Py.Repr(v1)}");

        // --- versionless delete: a delete marker is created ---------------------------------------
        var d1 = await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key });
        Ensure(d1.DeleteMarker == "true", $"versionless delete returned DeleteMarker={Py.Repr(d1.DeleteMarker)}");
        var marker = d1.VersionId;
        var cDm = (await paths.Await(classic, Classic(key, "ObjectRemoved:DeleteMarkerCreated"), "classic ObjectRemoved:DeleteMarkerCreated"))[0];
        var eDel = (await paths.Await(bridge, Bridge(key, "Object Deleted"), "EventBridge 'Object Deleted'"))[0];
        Ensure(
            (string?)cDm["s3"]!["object"]!["versionId"] == marker,
            $"classic DeleteMarkerCreated names version {Py.Repr((string?)cDm["s3"]!["object"]!["versionId"])}, " +
            $"but the marker S3 returned is {Py.Repr(marker)}");
        Ensure(
            (string?)eDel["detail"]!["reason"] == "DeleteObject",
            $"'Object Deleted' reason {Py.Repr((string?)eDel["detail"]!["reason"])}, expected 'DeleteObject'");
        Ensure(
            (string?)eDel["detail"]!["deletion-type"] == "Delete Marker Created",
            $"a versionless delete on a versioned bucket must be 'Delete Marker Created', got {Py.Repr((string?)eDel["detail"]!["deletion-type"])}");
        Ensure(
            (string?)eDel["detail"]!["object"]!["version-id"] == marker,
            $"'Object Deleted' names version {Py.Repr((string?)eDel["detail"]!["object"]!["version-id"])}, expected the marker {Py.Repr(marker)}");
        var stack = await s3.ListVersionsAsync(new ListVersionsRequest { BucketName = bucket, Prefix = key });
        var dataVersions = (stack.Versions ?? []).Where(v => v.IsDeleteMarker != true).Select(v => v.VersionId).ToList();
        var markers = (stack.Versions ?? []).Where(v => v.IsDeleteMarker == true).Select(v => v.VersionId).ToList();
        Ensure(dataVersions.SequenceEqual([v1]), $"the data version must survive a delete marker; versions are {Py.Repr(dataVersions)}");
        Ensure(markers.SequenceEqual([marker]), $"delete markers are {Py.Repr(markers)}, expected [{Py.Repr(marker)}]");

        // --- version-specific delete: permanent ---------------------------------------------------
        var d2 = await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key, VersionId = v1 });
        Ensure(d2.VersionId == v1 && d2.DeleteMarker != "true", $"deleting version {Py.Repr(v1)} returned VersionId={Py.Repr(d2.VersionId)} DeleteMarker={Py.Repr(d2.DeleteMarker)}");
        var cPerm = (await paths.Await(classic, Classic(key, "ObjectRemoved:Delete"), "classic ObjectRemoved:Delete"))[0];
        var ePerm = (await paths.Await(
            bridge,
            seen => [.. Bridge(key, "Object Deleted")(seen).Where(e => (string?)e["detail"]!["deletion-type"] != "Delete Marker Created")],
            "EventBridge permanent 'Object Deleted'"))[0];
        Ensure(
            (string?)cPerm["s3"]!["object"]!["versionId"] == v1,
            $"classic ObjectRemoved:Delete names version {Py.Repr((string?)cPerm["s3"]!["object"]!["versionId"])}, expected {Py.Repr(v1)}");
        Ensure(
            (string?)ePerm["detail"]!["deletion-type"] == "Permanently Deleted",
            $"deleting a specific version must be 'Permanently Deleted', got {Py.Repr((string?)ePerm["detail"]!["deletion-type"])}");
        Ensure(
            (string?)ePerm["detail"]!["reason"] == "DeleteObject",
            $"permanent delete reason {Py.Repr((string?)ePerm["detail"]!["reason"])}, expected 'DeleteObject'");
        Ensure(
            (string?)ePerm["detail"]!["object"]!["version-id"] == v1,
            $"permanent 'Object Deleted' names version {Py.Repr((string?)ePerm["detail"]!["object"]!["version-id"])}, expected {Py.Repr(v1)}");

        // invariant: exactly the three transitions, each named once per vocabulary
        var cnames = Classic(key)(classic.Seen).Select(r => ((string?)r["eventName"], (string?)r["s3"]!["object"]!["versionId"])).ToHashSet();
        Ensure(
            cnames.SetEquals([("ObjectCreated:Put", v1), ("ObjectRemoved:DeleteMarkerCreated", marker), ("ObjectRemoved:Delete", v1)]),
            $"classic path delivered {Py.Repr(cnames.Order().ToList())}");
        var etypes = Bridge(key)(bridge.Seen)
            .Select(e => ((string?)e["detail-type"], (string?)e["detail"]!["deletion-type"], (string?)e["detail"]!["object"]!["version-id"]))
            .ToHashSet();
        Ensure(
            etypes.SetEquals([("Object Created", null, v1), ("Object Deleted", "Delete Marker Created", marker), ("Object Deleted", "Permanently Deleted", v1)]),
            $"EventBridge path delivered {Py.Repr(etypes.Select(t => t.ToString()).Order(StringComparer.Ordinal).ToList())}");
    }
}

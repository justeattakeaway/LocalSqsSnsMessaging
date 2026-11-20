// Translated from behavioral/events__s3__sqs/check_obs_s3_3_tags_deleted_withheld_from_tags_added_routes.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;
using static InMemoryAws.Tests.AwsEvaluationSuite.EventsS3Sqs.ObsS3EventPaths;

namespace InMemoryAws.Tests.AwsEvaluationSuite.EventsS3Sqs;

/// <summary>events__s3__sqs.obs-s3-3-tags-deleted-withheld-from-tags-added-routes</summary>
[Category("AwsEvaluationSuite")]
public sealed class ObsS33TagsDeletedWithheldFromTagsAddedRoutes
{
    private const string Check = "obs-s3-3-tags-deleted-withheld-from-tags-added-routes";

    /// <summary>
    /// DeleteObjectTagging reaches only the tag-delete routes and PutObjectTagging only the tag-add
    /// routes, on both the classic ObjectTagging subscription and the EventBridge rule paths.
    /// </summary>
    [Test]
    [DisplayName("events__s3__sqs.obs-s3-3-tags-deleted-withheld-from-tags-added-routes")]
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

        var bucket = await paths.Bucket(s3);
        var bucketArn = $"arn:{partition}:s3:::{bucket}";
        var (cputUrl, cputArn) = await paths.Queue(sqs);
        await Allow(sqs, cputUrl, cputArn, "s3.amazonaws.com", bucketArn, account);
        var (cdelUrl, cdelArn) = await paths.Queue(sqs);
        await Allow(sqs, cdelUrl, cdelArn, "s3.amazonaws.com", bucketArn, account);
        var (eaddUrl, _) = await paths.RuleToQueue(ev, sqs, new JsonObject
        {
            ["source"] = new JsonArray("aws.s3"),
            ["detail-type"] = new JsonArray("Object Tags Added"),
            ["detail"] = new JsonObject { ["bucket"] = new JsonObject { ["name"] = new JsonArray(bucket) } },
        }, account);
        var (edelUrl, _) = await paths.RuleToQueue(ev, sqs, new JsonObject
        {
            ["source"] = new JsonArray("aws.s3"),
            ["detail-type"] = new JsonArray("Object Tags Deleted"),
            ["detail"] = new JsonObject { ["bucket"] = new JsonObject { ["name"] = new JsonArray(bucket) } },
        }, account);
        await paths.WireBucket(s3, bucket, [(cputArn, ["s3:ObjectTagging:Put"]), (cdelArn, ["s3:ObjectTagging:Delete"])]);
        var (cput, cdel) = (new Inbox(sqs, cputUrl, "classic-put"), new Inbox(sqs, cdelUrl, "classic-delete"));
        var (eadd, edel) = (new Inbox(sqs, eaddUrl, "eb-added"), new Inbox(sqs, edelUrl, "eb-deleted"));

        async Task<string> Fire(int i)
        {
            var k = $"warm-{i}";
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = k, ContentBody = "sim2real-warm" });
            await s3.PutObjectTaggingAsync(new PutObjectTaggingRequest { BucketName = bucket, Key = k, Tagging = new Tagging { TagSet = [new Tag { Key = "w", Value = "1" }] } });
            await s3.DeleteObjectTaggingAsync(new DeleteObjectTaggingRequest { BucketName = bucket, Key = k });
            return k;
        }

        await WarmUp(Fire, [
            (cput, k => Classic(k, "ObjectTagging:Put")),
            (cdel, k => Classic(k, "ObjectTagging:Delete")),
            (eadd, k => Bridge(k, "Object Tags Added")),
            (edel, k => Bridge(k, "Object Tags Deleted")),
        ]);

        var key = FidelityContext.UniqueName("tags-");
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "sim2real-tag-routing" });

        // --- positive control for the add-routes ----------------------------------------------
        var t0 = FidelityTime.StartNew();
        await s3.PutObjectTaggingAsync(new PutObjectTaggingRequest { BucketName = bucket, Key = key, Tagging = new Tagging { TagSet = [new Tag { Key = "k", Value = "1" }] } });
        await paths.Await(cput, Classic(key, "ObjectTagging:Put"), "classic ObjectTagging:Put (control)");
        await paths.Await(eadd, Bridge(key, "Object Tags Added"), "EventBridge 'Object Tags Added' (control)");
        var addLatency = t0.Elapsed.TotalSeconds;

        // --- the act under test, with its own routes as the barrier ---------------------------
        var t1 = FidelityTime.StartNew();
        await s3.DeleteObjectTaggingAsync(new DeleteObjectTaggingRequest { BucketName = bucket, Key = key });
        await paths.Await(cdel, Classic(key, "ObjectTagging:Delete"), "classic ObjectTagging:Delete (barrier)");
        await paths.Await(edel, Bridge(key, "Object Tags Deleted"), "EventBridge 'Object Tags Deleted' (barrier)");
        var delLatency = t1.Elapsed.TotalSeconds;
        Ensure(
            (await s3.GetObjectTaggingAsync(new GetObjectTaggingRequest { BucketName = bucket, Key = key })).Tagging is not { Count: > 0 },
            "tags were not deleted");

        var window = AbsenceWindow(Math.Max(addLatency, delLatency));
        foreach (var inbox in new[] { cput, eadd, cdel, edel })
        {
            await inbox.Soak(window / 4);
        }

        var leakedC = Classic(key)(cput.Seen).Select(r => (string?)r["eventName"]).Where(n => n != "ObjectTagging:Put").ToList();
        var leakedE = Bridge(key)(eadd.Seen).Select(e => (string?)e["detail-type"]).Where(t => t != "Object Tags Added").ToList();
        Ensure(
            leakedC.Count == 0,
            $"the s3:ObjectTagging:Put subscription received {Py.Repr(leakedC)} over {window:F0}s after " +
            "DeleteObjectTagging; its Delete twin on the same bucket received the deletion");
        Ensure(
            leakedE.Count == 0,
            $"the 'Object Tags Added' rule's queue received {Py.Repr(leakedE)} over {window:F0}s after " +
            "DeleteObjectTagging; the 'Object Tags Deleted' rule received the deletion");
        var wrongC = Classic(key)(cdel.Seen).Select(r => (string?)r["eventName"]).Where(n => n != "ObjectTagging:Delete").ToList();
        var wrongE = Bridge(key)(edel.Seen).Select(e => (string?)e["detail-type"]).Where(t => t != "Object Tags Deleted").ToList();
        Ensure(
            wrongC.Count == 0 && wrongE.Count == 0,
            $"the delete-routes must never see the earlier PutObjectTagging; classic-delete got {Py.Repr(wrongC)}, eb-deleted got {Py.Repr(wrongE)}");

        // --- closure: the add-routes are still live after the soak ----------------------------
        var (beforeC, beforeE) = (Classic(key)(cput.Seen).Count, Bridge(key)(eadd.Seen).Count);
        await s3.PutObjectTaggingAsync(new PutObjectTaggingRequest { BucketName = bucket, Key = key, Tagging = new Tagging { TagSet = [new Tag { Key = "k", Value = "2" }] } });
        await paths.Await(cput, seen => [.. Classic(key, "ObjectTagging:Put")(seen).Skip(beforeC)], "classic ObjectTagging:Put after the soak (closure)");
        await paths.Await(eadd, seen => [.. Bridge(key, "Object Tags Added")(seen).Skip(beforeE)], "EventBridge 'Object Tags Added' after the soak (closure)");
    }
}

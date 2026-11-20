// Translated from behavioral/events__s3__sqs/check_obs_s3_3_object_tagging_put_and_delete_reach_both_paths.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;
using static InMemoryAws.Tests.AwsEvaluationSuite.EventsS3Sqs.ObsS3EventPaths;

namespace InMemoryAws.Tests.AwsEvaluationSuite.EventsS3Sqs;

/// <summary>events__s3__sqs.obs-s3-3-object-tagging-put-and-delete-reach-both-paths</summary>
[Category("AwsEvaluationSuite")]
public sealed class ObsS33ObjectTaggingPutAndDeleteReachBothPaths
{
    private const string Check = "obs-s3-3-object-tagging-put-and-delete-reach-both-paths";

    /// <summary>
    /// PutObjectTagging and DeleteObjectTagging reach both delivery paths under their own names:
    /// classic <c>ObjectTagging:Put</c>/<c>:Delete</c>, EventBridge "Object Tags Added"/"Object Tags
    /// Deleted", and both paths name the same S3 request.
    /// </summary>
    [Test]
    [DisplayName("events__s3__sqs.obs-s3-3-object-tagging-put-and-delete-reach-both-paths")]
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
        var (curl, carn) = await paths.Queue(sqs);
        await Allow(sqs, curl, carn, "s3.amazonaws.com", $"arn:{partition}:s3:::{bucket}", account);
        var (eurl, _) = await paths.RuleToQueue(ev, sqs, new JsonObject
        {
            ["source"] = new JsonArray("aws.s3"),
            ["detail"] = new JsonObject { ["bucket"] = new JsonObject { ["name"] = new JsonArray(bucket) } },
        }, account);
        await paths.WireBucket(s3, bucket, [(carn, ["s3:ObjectCreated:Put", "s3:ObjectTagging:*"])]);
        var (classic, bridge) = (new Inbox(sqs, curl, "classic"), new Inbox(sqs, eurl, "eventbridge"));

        async Task<string> Fire(int i)
        {
            var k = $"warm-{i}";
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = k, ContentBody = "sim2real-warm" });
            return k;
        }

        await WarmUp(Fire, [(classic, k => Classic(k, "ObjectCreated:Put")), (bridge, k => Bridge(k, "Object Created"))]);

        // --- warm-up: both paths deliver a plain PutObject before the operations under test --------
        var key = FidelityContext.UniqueName("tagged-");
        var put = await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "sim2real-tagging" });
        var etag = put.ETag.Trim('"');
        await paths.Await(classic, Classic(key, "ObjectCreated:Put"), "classic ObjectCreated:Put warm-up");
        await paths.Await(bridge, Bridge(key, "Object Created"), "EventBridge 'Object Created' warm-up");

        // --- PutObjectTagging ---------------------------------------------------------------------
        // The classic eventName has no `s3:` prefix (`ObjectTagging:Put`), unlike the subscription type.
        var resp = await s3.PutObjectTaggingAsync(new PutObjectTaggingRequest
        {
            BucketName = bucket,
            Key = key,
            Tagging = new Tagging { TagSet = [new Tag { Key = "colour", Value = "teal" }] },
        });
        var putReq = resp.ResponseMetadata.RequestId;
        var crec = (await paths.Await(classic, Classic(key, "ObjectTagging:Put"), "classic ObjectTagging:Put"))[0];
        var eenv = (await paths.Await(bridge, Bridge(key, "Object Tags Added"), "EventBridge 'Object Tags Added'"))[0];
        Ensure((string?)crec["eventSource"] == "aws:s3", $"eventSource was {Py.Repr((string?)crec["eventSource"])}");
        Ensure(
            (string?)crec["awsRegion"] == ctx.Region,
            $"classic record awsRegion {Py.Repr((string?)crec["awsRegion"])} != bucket region {Py.Repr(ctx.Region)}");
        Ensure((string?)crec["s3"]!["bucket"]!["name"] == bucket, $"bucket {Py.Repr((string?)crec["s3"]!["bucket"]!["name"])}");
        Ensure(
            (string?)crec["s3"]!["object"]!["eTag"] == etag,
            $"classic ObjectTagging:Put eTag {Py.Repr((string?)crec["s3"]!["object"]!["eTag"])} != object ETag {Py.Repr(etag)}");
        Ensure(
            (string?)eenv["detail"]!["object"]!["etag"] == etag,
            $"'Object Tags Added' etag {Py.Repr((string?)eenv["detail"]!["object"]!["etag"])} != object ETag {Py.Repr(etag)}");
        Ensure(
            (string?)crec["responseElements"]!["x-amz-request-id"] == putReq,
            $"classic record names request {Py.Repr((string?)crec["responseElements"]!["x-amz-request-id"])}, but the " +
            $"PutObjectTagging response was request {Py.Repr(putReq)}");
        Ensure(
            (string?)eenv["detail"]!["request-id"] == putReq,
            $"'Object Tags Added' names request {Py.Repr((string?)eenv["detail"]!["request-id"])}, but the " +
            $"PutObjectTagging response was request {Py.Repr(putReq)}");
        var tagset = (await s3.GetObjectTaggingAsync(new GetObjectTaggingRequest { BucketName = bucket, Key = key })).Tagging;
        Ensure(
            tagset.Count == 1 && tagset[0].Key == "colour" && tagset[0].Value == "teal",
            $"GetObjectTagging returned {Py.Repr(tagset.Select(t => (t.Key, t.Value)).ToList())}");

        // --- DeleteObjectTagging ------------------------------------------------------------------
        var delResp = await s3.DeleteObjectTaggingAsync(new DeleteObjectTaggingRequest { BucketName = bucket, Key = key });
        var delReq = delResp.ResponseMetadata.RequestId;
        crec = (await paths.Await(classic, Classic(key, "ObjectTagging:Delete"), "classic ObjectTagging:Delete"))[0];
        eenv = (await paths.Await(bridge, Bridge(key, "Object Tags Deleted"), "EventBridge 'Object Tags Deleted'"))[0];
        Ensure(
            (string?)crec["responseElements"]!["x-amz-request-id"] == delReq,
            $"classic ObjectTagging:Delete names request {Py.Repr((string?)crec["responseElements"]!["x-amz-request-id"])}, expected {Py.Repr(delReq)}");
        Ensure(
            (string?)eenv["detail"]!["request-id"] == delReq,
            $"'Object Tags Deleted' names request {Py.Repr((string?)eenv["detail"]!["request-id"])}, expected {Py.Repr(delReq)}");
        Ensure(
            (string?)eenv["detail"]!["object"]!["etag"] == etag,
            $"'Object Tags Deleted' etag {Py.Repr((string?)eenv["detail"]!["object"]!["etag"])} != {Py.Repr(etag)}");
        tagset = (await s3.GetObjectTaggingAsync(new GetObjectTaggingRequest { BucketName = bucket, Key = key })).Tagging;
        Ensure(tagset is not { Count: > 0 }, $"GetObjectTagging after delete returned {Py.Repr(tagset?.Select(t => (t.Key, t.Value)).ToList())}");

        // --- invariant: each path used only its own vocabulary, and nothing else was emitted -------
        var cnames = Classic(key)(classic.Seen).Select(r => (string?)r["eventName"]).ToHashSet();
        var enames = Bridge(key)(bridge.Seen).Select(e => (string?)e["detail-type"]).ToHashSet();
        Ensure(
            cnames.SetEquals(["ObjectCreated:Put", "ObjectTagging:Put", "ObjectTagging:Delete"]),
            $"classic path delivered event names {Py.Repr(cnames.Order().ToList())} for {Py.Repr(key)}");
        Ensure(
            enames.SetEquals(["Object Created", "Object Tags Added", "Object Tags Deleted"]),
            $"EventBridge path delivered detail-types {Py.Repr(enames.Order().ToList())} for {Py.Repr(key)}");
    }
}

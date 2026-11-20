// Translated from behavioral/events__s3__sqs/check_obs_s3_3_copy_object_reaches_both_paths_with_copy_reason.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;
using static InMemoryAws.Tests.AwsEvaluationSuite.EventsS3Sqs.ObsS3EventPaths;

namespace InMemoryAws.Tests.AwsEvaluationSuite.EventsS3Sqs;

/// <summary>events__s3__sqs.obs-s3-3-copy-object-reaches-both-paths-with-copy-reason</summary>
[Category("AwsEvaluationSuite")]
public sealed class ObsS33CopyObjectReachesBothPathsWithCopyReason
{
    private const string Check = "obs-s3-3-copy-object-reaches-both-paths-with-copy-reason";

    /// <summary>
    /// CopyObject reaches both notification paths as a copy (ObjectCreated:Copy and detail.reason
    /// CopyObject) with the destination's ETag and size, while the source PutObject is
    /// ObjectCreated:Put and reason PutObject.
    /// </summary>
    [Test]
    [DisplayName("events__s3__sqs.obs-s3-3-copy-object-reaches-both-paths-with-copy-reason")]
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
            ["detail-type"] = new JsonArray("Object Created"),
            ["detail"] = new JsonObject { ["bucket"] = new JsonObject { ["name"] = new JsonArray(bucket) } },
        }, account);
        await paths.WireBucket(s3, bucket, [(carn, ["s3:ObjectCreated:*"])]);
        var (classic, bridge) = (new Inbox(sqs, curl, "classic"), new Inbox(sqs, eurl, "eventbridge"));

        async Task<string> Fire(int i)
        {
            var k = $"warm-{i}";
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = k, ContentBody = "sim2real-warm" });
            return k;
        }

        await WarmUp(Fire, [(classic, k => Classic(k, "ObjectCreated:Put")), (bridge, k => Bridge(k, "Object Created"))]);

        const string body = "sim2real-copy-source-bytes";
        var srcKey = FidelityContext.UniqueName("src-");
        var dstKey = FidelityContext.UniqueName("dst-");
        var put = await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = srcKey, ContentBody = body });
        var putReq = put.ResponseMetadata.RequestId;
        var srcC = (await paths.Await(classic, Classic(srcKey, "ObjectCreated:Put"), "classic Put for source"))[0];
        var srcE = (await paths.Await(bridge, Bridge(srcKey, "Object Created"), "EventBridge source creation"))[0];
        Ensure((string?)srcE["detail"]!["reason"] == "PutObject", $"the source was created by PutObject; reason was {Py.Repr((string?)srcE["detail"]!["reason"])}");
        Ensure(
            (string?)srcC["responseElements"]!["x-amz-request-id"] == putReq,
            $"classic Put names request {Py.Repr((string?)srcC["responseElements"]!["x-amz-request-id"])}, expected {Py.Repr(putReq)}");

        var cp = await s3.CopyObjectAsync(new CopyObjectRequest { SourceBucket = bucket, SourceKey = srcKey, DestinationBucket = bucket, DestinationKey = dstKey });
        var cpReq = cp.ResponseMetadata.RequestId;
        var dstEtag = cp.ETag.Trim('"');
        var headDst = await s3.GetObjectMetadataAsync(bucket, dstKey);
        Ensure(headDst.ContentLength == body.Length, $"copy is {headDst.ContentLength} bytes");

        var crec = (await paths.Await(classic, Classic(dstKey), "classic notification for the copy"))[0];
        var eenv = (await paths.Await(bridge, Bridge(dstKey, "Object Created"), "EventBridge copy creation"))[0];
        Ensure((string?)crec["eventName"] == "ObjectCreated:Copy", $"CopyObject must notify as 'ObjectCreated:Copy', got {Py.Repr((string?)crec["eventName"])}");
        Ensure(
            (string?)crec["s3"]!["object"]!["eTag"] == dstEtag,
            $"classic Copy eTag {Py.Repr((string?)crec["s3"]!["object"]!["eTag"])} != CopyObject ETag {Py.Repr(dstEtag)}");
        Ensure(
            (long?)crec["s3"]!["object"]!["size"] == body.Length,
            $"classic Copy size {crec["s3"]!["object"]!["size"]?.ToJsonString()} != {body.Length}");
        Ensure(
            (string?)crec["responseElements"]!["x-amz-request-id"] == cpReq,
            $"classic Copy names request {Py.Repr((string?)crec["responseElements"]!["x-amz-request-id"])}, but CopyObject was request {Py.Repr(cpReq)}");
        Ensure(
            (string?)eenv["detail"]!["reason"] == "CopyObject",
            $"an object created by CopyObject must carry reason 'CopyObject', got {Py.Repr((string?)eenv["detail"]!["reason"])}");
        Ensure(
            (string?)eenv["detail"]!["object"]!["etag"] == dstEtag,
            $"EventBridge copy etag {Py.Repr((string?)eenv["detail"]!["object"]!["etag"])} != {Py.Repr(dstEtag)}");
        Ensure(
            (long?)eenv["detail"]!["object"]!["size"] == body.Length,
            $"EventBridge copy size {eenv["detail"]!["object"]!["size"]?.ToJsonString()} != {body.Length}");
        Ensure(
            (string?)eenv["detail"]!["request-id"] == cpReq,
            $"EventBridge copy names request {Py.Repr((string?)eenv["detail"]!["request-id"])}, expected {Py.Repr(cpReq)}");

        // invariant: no path ever labelled the copy as a put, or the put as a copy
        var cnames = Classic(srcKey)(classic.Seen).Concat(Classic(dstKey)(classic.Seen))
            .Select(r => ((string?)r["s3"]!["object"]!["key"], (string?)r["eventName"])).ToHashSet();
        Ensure(
            cnames.SetEquals([(srcKey, "ObjectCreated:Put"), (dstKey, "ObjectCreated:Copy")]),
            $"classic path labelled the two creations {Py.Repr(cnames.Order().ToList())}");
        var reasons = Bridge(srcKey)(bridge.Seen).Concat(Bridge(dstKey)(bridge.Seen))
            .Select(e => ((string?)e["detail"]!["object"]!["key"], (string?)e["detail"]!["reason"])).ToHashSet();
        Ensure(
            reasons.SetEquals([(srcKey, "PutObject"), (dstKey, "CopyObject")]),
            $"EventBridge path labelled the two creations {Py.Repr(reasons.Order().ToList())}");
    }
}

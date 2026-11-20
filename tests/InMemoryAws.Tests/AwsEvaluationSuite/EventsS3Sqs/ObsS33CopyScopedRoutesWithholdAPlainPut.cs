// Translated from behavioral/events__s3__sqs/check_obs_s3_3_copy_scoped_routes_withhold_a_plain_put.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;
using static InMemoryAws.Tests.AwsEvaluationSuite.EventsS3Sqs.ObsS3EventPaths;

namespace InMemoryAws.Tests.AwsEvaluationSuite.EventsS3Sqs;

/// <summary>events__s3__sqs.obs-s3-3-copy-scoped-routes-withhold-a-plain-put</summary>
[Category("AwsEvaluationSuite")]
public sealed class ObsS33CopyScopedRoutesWithholdAPlainPut
{
    private const string Check = "obs-s3-3-copy-scoped-routes-withhold-a-plain-put";

    /// <summary>
    /// A PutObject reaches the Put-scoped and unfiltered routes but not the classic
    /// ObjectCreated:Copy subscription or the CopyObject-reason rule, and both copy routes then
    /// receive a CopyObject.
    /// </summary>
    [Test]
    [DisplayName("events__s3__sqs.obs-s3-3-copy-scoped-routes-withhold-a-plain-put")]
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
        var (ccopyUrl, ccopyArn) = await paths.Queue(sqs);
        await Allow(sqs, ccopyUrl, ccopyArn, "s3.amazonaws.com", bucketArn, account);
        var (cputUrl, cputArn) = await paths.Queue(sqs);
        await Allow(sqs, cputUrl, cputArn, "s3.amazonaws.com", bucketArn, account);
        var (ecopyUrl, _) = await paths.RuleToQueue(ev, sqs, new JsonObject
        {
            ["source"] = new JsonArray("aws.s3"),
            ["detail-type"] = new JsonArray("Object Created"),
            ["detail"] = new JsonObject { ["bucket"] = new JsonObject { ["name"] = new JsonArray(bucket) }, ["reason"] = new JsonArray("CopyObject") },
        }, account);
        var (eallUrl, _) = await paths.RuleToQueue(ev, sqs, new JsonObject
        {
            ["source"] = new JsonArray("aws.s3"),
            ["detail-type"] = new JsonArray("Object Created"),
            ["detail"] = new JsonObject { ["bucket"] = new JsonObject { ["name"] = new JsonArray(bucket) } },
        }, account);
        await paths.WireBucket(s3, bucket, [(ccopyArn, ["s3:ObjectCreated:Copy"]), (cputArn, ["s3:ObjectCreated:Put"])]);
        var (ccopy, cput) = (new Inbox(sqs, ccopyUrl, "classic-copy"), new Inbox(sqs, cputUrl, "classic-put"));
        var (ecopy, eall) = (new Inbox(sqs, ecopyUrl, "eb-copy"), new Inbox(sqs, eallUrl, "eb-all-created"));

        async Task<string> Fire(int i)
        {
            var k = $"warm-{i}";
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = $"{k}-src", ContentBody = "sim2real-warm" });
            await s3.CopyObjectAsync(new CopyObjectRequest { SourceBucket = bucket, SourceKey = $"{k}-src", DestinationBucket = bucket, DestinationKey = $"{k}-dst" });
            return k;
        }

        await WarmUp(Fire, [
            (cput, k => Classic($"{k}-src", "ObjectCreated:Put")),
            (ccopy, k => Classic($"{k}-dst", "ObjectCreated:Copy")),
            (eall, k => Bridge($"{k}-src", "Object Created")),
            (ecopy, k => Bridge($"{k}-dst", "Object Created")),
        ]);

        var srcKey = FidelityContext.UniqueName("src-");
        var dstKey = FidelityContext.UniqueName("dst-");

        // --- the act under test: a plain put, with the control routes as the barrier ----------
        var t0 = FidelityTime.StartNew();
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = srcKey, ContentBody = "sim2real-copy-routing" });
        await paths.Await(cput, Classic(srcKey, "ObjectCreated:Put"), "classic ObjectCreated:Put (barrier)");
        var putEnv = (await paths.Await(eall, Bridge(srcKey, "Object Created"), "unfiltered EventBridge (barrier)"))[0];
        Ensure((string?)putEnv["detail"]!["reason"] == "PutObject", $"the barrier event must be the put; reason {Py.Repr((string?)putEnv["detail"]!["reason"])}");
        var window = AbsenceWindow(t0.Elapsed.TotalSeconds);
        await ccopy.Soak(window / 2);
        await ecopy.Soak(window / 2);
        Ensure(
            Classic(srcKey)(ccopy.Seen).Count == 0,
            $"the s3:ObjectCreated:Copy-only subscription received {Py.Repr(Classic(srcKey)(ccopy.Seen).Select(r => (string?)r["eventName"]).ToList())} " +
            $"for a PutObject over {window:F0}s; the ObjectCreated:Put twin on this bucket received it");
        Ensure(
            Bridge(srcKey)(ecopy.Seen).Count == 0,
            $"the reason=CopyObject rule's queue received {Py.Repr(Bridge(srcKey)(ecopy.Seen).Select(e => (string?)e["detail"]!["reason"]).ToList())} " +
            $"for a PutObject over {window:F0}s; the unfiltered rule received it");

        // --- closure: the silent routes receive the copy --------------------------------------
        await s3.CopyObjectAsync(new CopyObjectRequest { SourceBucket = bucket, SourceKey = srcKey, DestinationBucket = bucket, DestinationKey = dstKey });
        var crec = (await paths.Await(ccopy, Classic(dstKey), "classic ObjectCreated:Copy (closure)"))[0];
        Ensure((string?)crec["eventName"] == "ObjectCreated:Copy", $"copy route got {Py.Repr((string?)crec["eventName"])}");
        var eenv = (await paths.Await(ecopy, Bridge(dstKey, "Object Created"), "reason=CopyObject rule (closure)"))[0];
        Ensure((string?)eenv["detail"]!["reason"] == "CopyObject", $"copy rule got reason {Py.Repr((string?)eenv["detail"]!["reason"])}");
        await paths.Await(eall, Bridge(dstKey, "Object Created"), "unfiltered EventBridge sees the copy too");
        await cput.Soak(2);
        var copiesOnPut = Classic(dstKey)(cput.Seen).Select(r => (string?)r["eventName"]).ToList();
        Ensure(copiesOnPut.Count == 0, $"the s3:ObjectCreated:Put subscription must not receive the copy; got {Py.Repr(copiesOnPut)}");
        Ensure(
            Classic(srcKey)(ccopy.Seen).Count == 0 && Bridge(srcKey)(ecopy.Seen).Count == 0,
            "the copy-routes received an event naming the SOURCE key; CopyObject notifies for the destination only");
    }
}

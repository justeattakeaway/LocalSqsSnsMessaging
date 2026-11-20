// Translated from behavioral/events__s3__sqs/check_xsvc_s3_1_acl_update_and_tag_delete_route_to_their_own_rules.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.EventsS3Sqs;

/// <summary>events__s3__sqs.xsvc-s3-1-acl-update-and-tag-delete-route-to-their-own-rules</summary>
[Category("AwsEvaluationSuite")]
public sealed class XsvcS31AclUpdateAndTagDeleteRouteToTheirOwnRules
{
    private const string Check = "xsvc-s3-1-acl-update-and-tag-delete-route-to-their-own-rules";
    private const string Prefix = "sim2real-xsvc-s3a-";
    private const string Created = "Object Created";
    private const string Acl = "Object ACL Updated";
    private const string TagsDeleted = "Object Tags Deleted";

    /// <summary>
    /// One rule and one queue per detail-type. Every queue is the positive control for its own type
    /// and an absence target for the other two, on wiring identical except for that predicate.
    /// </summary>
    private static readonly string[] Types = [Created, Acl, TagsDeleted];

    private const double DeliveryBudgetS = 180.0;
    private const double MaxCredibleDeliveryWaitS = 180.0;
    private const double AbsenceFloorBudgetS = 30.0;
    private const double AbsenceCeilingBudgetS = 90.0;

    /// <summary>
    /// PutObjectAcl emits "Object ACL Updated" and DeleteObjectTagging "Object Tags Deleted", each
    /// reaching only the rule naming its detail-type; after a soak every queue holds only its own
    /// type, and S3 reads back a single FULL_CONTROL grant and an empty tag set.
    /// </summary>
    [Test]
    [DisplayName("events__s3__sqs.xsvc-s3-1-acl-update-and-tag-delete-route-to-their-own-rules")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var (ev, s3, sqs) = (ctx.Events, ctx.S3, ctx.Sqs);
        var account = ctx.Account;

        var bucket = await MakeBucket(ctx, s3);
        var boxes = new Dictionary<string, XsvcS3Mailbox>(StringComparer.Ordinal);
        foreach (var detailType in Types)
        {
            var (url, arn) = await XsvcS3Mailbox.MakeQueue(ctx, sqs, account, Prefix, Check);
            await XsvcS3Mailbox.MakeRule(ctx, ev, new JsonObject
            {
                ["source"] = new JsonArray("aws.s3"),
                ["detail-type"] = new JsonArray(detailType),
                ["detail"] = new JsonObject { ["bucket"] = new JsonObject { ["name"] = new JsonArray(bucket) } },
            }, arn, Prefix, Check);
            boxes[detailType] = new XsvcS3Mailbox(sqs, url, $"{Py.Repr(detailType)} queue");
        }

        await s3.PutBucketNotificationAsync(new PutBucketNotificationRequest { BucketName = bucket, EventBridgeConfiguration = new EventBridgeConfiguration() });
        var enabled = await s3.GetBucketNotificationAsync(bucket);
        Ensure(enabled.EventBridgeConfiguration is not null, "bucket must be wired to EventBridge; GetBucketNotificationConfiguration -> no EventBridgeConfiguration");

        const string key = "shared/doc.txt";
        // Retried: a freshly created rule/target can silently drop its first delivered event, and
        // re-issuing the same PutObject is safe here (unversioned bucket, idempotent overwrite).
        var createdWait = FidelityTime.StartNew();
        double? createdLatency = null;
        while (createdLatency is null)
        {
            await s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket,
                Key = key,
                ContentBody = "sim2real-acl-tag-bytes",
                TagSet = [new Tag { Key = "team", Value = "sim2real" }, new Tag { Key = "stage", Value = "draft" }],
            });
            var remaining = DeliveryBudgetS - createdWait.Elapsed.TotalSeconds;
            Ensure(remaining > 0, $"{Py.Repr(Created)} for {Py.Repr(key)} never delivered after retries; detail-types seen: {Py.Repr(boxes[Created].Types().Order(StringComparer.Ordinal).ToList())}");
            (_, createdLatency) = await boxes[Created].Poll(e => (string?)e["detail"]!["object"]!["key"] == key, Math.Min(30.0, remaining));
        }

        var tagset = (await s3.GetObjectTaggingAsync(new GetObjectTaggingRequest { BucketName = bucket, Key = key })).Tagging;
        var sortedTags = tagset.Select(t => (t.Key, t.Value)).Order().ToList();
        Ensure(
            sortedTags.SequenceEqual([("stage", "draft"), ("team", "sim2real")]),
            $"the object must carry the two tags set at PutObject; GetObjectTagging -> {Py.Repr(sortedTags)}");

        // --- ACL update --------------------------------------------------------------------------
        // Retried: PutObjectAcl is idempotent/safe to repeat, and the same freshly-created-target
        // drop risk applies to this rule's first delivery too.
        var aclWait = FidelityTime.StartNew();
        JsonObject? aclEnv = null;
        double? aclLatency = null;
        while (aclLatency is null)
        {
            await s3.PutObjectAclAsync(new PutObjectAclRequest { BucketName = bucket, Key = key, ACL = S3CannedACL.BucketOwnerFullControl });
            var remaining = DeliveryBudgetS - aclWait.Elapsed.TotalSeconds;
            Ensure(remaining > 0, $"{Py.Repr(Acl)} for {Py.Repr(key)} never delivered after retries; detail-types seen: {Py.Repr(boxes[Acl].Types().Order(StringComparer.Ordinal).ToList())}");
            (aclEnv, aclLatency) = await boxes[Acl].Poll(e => (string?)e["detail"]!["object"]!["key"] == key, Math.Min(30.0, remaining));
        }

        Ensure((string?)aclEnv!["source"] == "aws.s3", $"source must be 'aws.s3', got {Py.Repr((string?)aclEnv["source"])}");
        Ensure((string?)aclEnv["detail-type"] == Acl, $"detail-type must be {Py.Repr(Acl)}, got {Py.Repr((string?)aclEnv["detail-type"])}");
        Ensure(
            (string?)aclEnv["detail"]!["bucket"]!["name"] == bucket,
            $"detail.bucket.name {Py.Repr((string?)aclEnv["detail"]!["bucket"]!["name"])} != {Py.Repr(bucket)}");
        var grants = (await s3.GetObjectAclAsync(new GetObjectAclRequest { BucketName = bucket, Key = key })).Grants;
        var permissions = grants.Select(g => g.Permission?.Value).ToList();
        Ensure(permissions.SequenceEqual(["FULL_CONTROL"]), $"after PutObjectAcl the object must hold one FULL_CONTROL grant; got {Py.Repr(permissions)}");
        // Barrier: the ACL event has arrived; no tagging-delete has been issued yet.
        Ensure(
            !boxes[TagsDeleted].Types().Contains(TagsDeleted),
            $"{Py.Repr(TagsDeleted)} arrived before DeleteObjectTagging was called: {Py.Repr(boxes[TagsDeleted].Bodies)}");

        // --- tag delete --------------------------------------------------------------------------
        // Retried: DeleteObjectTagging on an already-untagged object is a no-op that need not re-fire
        // the event, so a retry re-adds a tag before deleting it again.
        var tagsWait = FidelityTime.StartNew();
        JsonObject? tagsEnv = null;
        double? tagsLatency = null;
        await s3.DeleteObjectTaggingAsync(new DeleteObjectTaggingRequest { BucketName = bucket, Key = key });
        while (tagsLatency is null)
        {
            var remaining = DeliveryBudgetS - tagsWait.Elapsed.TotalSeconds;
            Ensure(remaining > 0, $"{Py.Repr(TagsDeleted)} for {Py.Repr(key)} never delivered after retries; detail-types seen: {Py.Repr(boxes[TagsDeleted].Types().Order(StringComparer.Ordinal).ToList())}");
            (tagsEnv, tagsLatency) = await boxes[TagsDeleted].Poll(e => (string?)e["detail"]!["object"]!["key"] == key, Math.Min(30.0, remaining));
            if (tagsLatency is null)
            {
                await s3.PutObjectTaggingAsync(new PutObjectTaggingRequest { BucketName = bucket, Key = key, Tagging = new Tagging { TagSet = [new Tag { Key = "retry", Value = "tag" }] } });
                await s3.DeleteObjectTaggingAsync(new DeleteObjectTaggingRequest { BucketName = bucket, Key = key });
            }
        }

        Ensure((string?)tagsEnv!["detail-type"] == TagsDeleted, $"detail-type must be {Py.Repr(TagsDeleted)}, got {Py.Repr((string?)tagsEnv["detail-type"])}");
        Ensure(
            (string?)tagsEnv["detail"]!["bucket"]!["name"] == bucket,
            $"detail.bucket.name {Py.Repr((string?)tagsEnv["detail"]!["bucket"]!["name"])} != {Py.Repr(bucket)}");
        var after = (await s3.GetObjectTaggingAsync(new GetObjectTaggingRequest { BucketName = bucket, Key = key })).Tagging;
        Ensure(after is not { Count: > 0 }, $"DeleteObjectTagging must empty the tag set; got {Py.Repr(after?.Select(t => (t.Key, t.Value)).ToList())}");

        // --- routing specificity: each queue holds only its own type -----------------------------
        var measured = Math.Max(createdLatency.Value, Math.Max(aclLatency.Value, tagsLatency.Value));
        Ensure(
            measured <= MaxCredibleDeliveryWaitS,
            $"slowest positive delivery took {measured:F1}s, above the {MaxCredibleDeliveryWaitS}s this check will calibrate an absence against");
        var window = Math.Min(Math.Max(AbsenceFloorBudgetS, 3.0 * measured), AbsenceCeilingBudgetS);
        foreach (var detailType in Types)
        {
            await boxes[detailType].Soak(window / Types.Length);
        }

        var strays = Types
            .Where(t => boxes[t].Types().Except([t]).Any())
            .ToDictionary(t => t, t => boxes[t].Types().Except([t]).Order(StringComparer.Ordinal).ToList());
        Ensure(
            strays.Count == 0,
            $"each single-type rule must receive only its own detail-type; over {window:F0}s the " +
            $"strays were {Py.Repr(strays)} (all three types were delivered, so every queue is wired)");
        var nonempty = new Dictionary<string, Dictionary<string, string>>();
        foreach (var t in Types)
        {
            var d = await boxes[t].Depth();
            if (!(d.Count == 2 && d.GetValueOrDefault("ApproximateNumberOfMessages") == "0" && d.GetValueOrDefault("ApproximateNumberOfMessagesNotVisible") == "0"))
            {
                nonempty[t] = d;
            }
        }

        Ensure(nonempty.Count == 0, $"every queue must be drained after the soak; not: {Py.Repr(nonempty)}");
    }

    /// <summary>
    /// ACLs ENABLED (BucketOwnerPreferred) so PutObjectAcl is an ordinary call, not one the
    /// bucket-owner-enforced default would refuse.
    /// </summary>
    private static async Task<string> MakeBucket(FidelityContext ctx, AmazonS3Client s3)
    {
        var bucket = FidelityContext.UniqueName(Prefix);
        var request = new PutBucketRequest { BucketName = bucket, UseClientRegion = false, ObjectOwnership = ObjectOwnership.BucketOwnerPreferred };
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

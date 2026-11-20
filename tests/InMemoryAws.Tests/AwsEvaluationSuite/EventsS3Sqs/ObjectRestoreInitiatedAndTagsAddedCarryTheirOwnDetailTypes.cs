// Translated from behavioral/events__s3__sqs/check_object_restore_initiated_and_tags_added_carry_their_own_detail_types.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.EventsS3Sqs;

/// <summary>events__s3__sqs.object-restore-initiated-and-tags-added-carry-their-own-detail-types</summary>
[Category("AwsEvaluationSuite")]
public sealed class ObjectRestoreInitiatedAndTagsAddedCarryTheirOwnDetailTypes
{
    private const double DeliveryBudgetS = 180.0;
    private const double MaxCredibleDeliveryWaitS = 120.0;

    // Absence window = max(floor, 3 x the slowest measured positive latency), capped by the ceiling.
    private const double AbsenceFloorBudgetS = 10.0;
    private const double AbsenceCeilingBudgetS = 360.0;

    private const string Created = "Object Created";
    private const string RestoreInitiated = "Object Restore Initiated";
    private const string TagsAdded = "Object Tags Added";

    /// <summary>
    /// One rule per detail-type, each with its own queue. Every queue is BOTH the positive control
    /// for its own type and the absence target for the other two.
    /// </summary>
    private static readonly string[] Types = [Created, RestoreInitiated, TagsAdded];

    /// <summary>
    /// RestoreObject and PutObjectTagging reach EventBridge under their own detail-types, and a rule
    /// matching exactly one detail-type never receives another (S3 must not stamp every event
    /// "Object Created").
    /// </summary>
    [Test]
    [DisplayName("events__s3__sqs.object-restore-initiated-and-tags-added-carry-their-own-detail-types")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;
        var s3 = ctx.S3;
        var sqs = ctx.Sqs;
        var account = ctx.Account;

        var bucket = FidelityContext.UniqueName("sim2real-behav-s3dt-");
        var create = new PutBucketRequest { BucketName = bucket, UseClientRegion = false };
        if (ctx.Region != "us-east-1")
        {
            create.PutBucketConfiguration = new PutBucketConfiguration { LocationConstraint = new BucketLocationConstraint(ctx.Region) };
        }

        await s3.PutBucketAsync(create);
        ctx.OnCleanup(() => s3.DeleteBucketAsync(bucket));
        await s3.PutBucketTaggingAsync(new PutBucketTaggingRequest
        {
            BucketName = bucket,
            TagSet = [new Amazon.S3.Model.Tag { Key = "sim2real", Value = "s3-eventbridge-detail-types" }],
        });

        // One single-detail-type rule and queue per type.
        var queues = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var detailType in Types)
        {
            var (url, arn) = await Queue(sqs, ctx, ctx.Region, account);
            queues[detailType] = url;
            var rule = FidelityContext.UniqueName("sim2real-behav-s3dt-");
            await ev.PutRuleAsync(new PutRuleRequest
            {
                Name = rule,
                State = RuleState.ENABLED,
                EventPattern = new JsonObject
                {
                    ["source"] = new JsonArray("aws.s3"),
                    ["detail-type"] = new JsonArray(detailType),
                    ["detail"] = new JsonObject { ["bucket"] = new JsonObject { ["name"] = new JsonArray(bucket) } },
                }.ToJsonString(),
                Tags = [new Amazon.EventBridge.Model.Tag { Key = "sim2real", Value = "s3-eventbridge-detail-types" }],
            });
            ctx.OnCleanup(() => ev.DeleteRuleAsync(new DeleteRuleRequest { Name = rule }));
            var resp = await ev.PutTargetsAsync(new PutTargetsRequest { Rule = rule, Targets = [new Target { Id = "q", Arn = arn }] });
            ctx.OnCleanup(() => ev.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = rule, Ids = ["q"] }));
            Ensure(
                resp.FailedEntryCount == 0,
                $"put_targets for the {Py.Repr(detailType)} rule reported {resp.FailedEntryCount} failures: {Py.Repr(resp.FailedEntries?.Select(f => f.ErrorMessage).ToList())}");
            var configured = (await ev.DescribeRuleAsync(new DescribeRuleRequest { Name = rule })).EventPattern;
            var types = JsonNode.Parse(configured)!["detail-type"]!.AsArray().Select(n => (string?)n).ToList();
            Ensure(
                types.SequenceEqual([detailType]),
                $"the {Py.Repr(detailType)} rule must match exactly that one detail-type; DescribeRule reports {Py.Repr(configured)}");
        }

        await s3.PutBucketNotificationAsync(new PutBucketNotificationRequest { BucketName = bucket, EventBridgeConfiguration = new EventBridgeConfiguration() });
        var enabled = await s3.GetBucketNotificationAsync(bucket);
        Ensure(
            enabled.EventBridgeConfiguration is not null,
            "the bucket must be wired to EventBridge before any event can be expected; GetBucketNotificationConfiguration reports no EventBridgeConfiguration");

        var key = FidelityContext.UniqueName("cold/obj-");

        // Object Created is the wiring's positive control; it runs BEFORE the restore because a
        // restore cannot be cancelled or repeated in one run.
        var putStarted = FidelityTime.StartNew();
        var put = await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = key,
            ContentBody = "sim2real-cold-bytes",
            StorageClass = S3StorageClass.Glacier,
        });
        ctx.OnCleanup(() => s3.DeleteObjectAsync(bucket, key));
        // EventBridge reports the ETag without the surrounding quotes PutObject returns.
        var expectedEtag = put.ETag.Trim('"');

        var created = await Collect(sqs, queues[Created], key, DeliveryBudgetS);
        var createdLatencyS = putStarted.Elapsed.TotalSeconds;
        Ensure(
            created.Count > 0,
            $"PutObject on an EventBridge-enabled bucket delivered no {Py.Repr(Created)} envelope for " +
            $"{Py.Repr(key)} within {DeliveryBudgetS}s; the object exists (ETag {Py.Repr(expectedEtag)}), so the EMISSION is what is missing");
        var envelope = created[0];
        Ensure((string?)envelope["source"] == "aws.s3", $"expected source 'aws.s3', got {Py.Repr((string?)envelope["source"])}");
        Ensure((string?)envelope["detail-type"] == Created, $"expected detail-type {Py.Repr(Created)}, got {Py.Repr((string?)envelope["detail-type"])}");
        Ensure(
            (string?)envelope["detail"]!["bucket"]!["name"] == bucket,
            $"expected detail.bucket.name {Py.Repr(bucket)}, got {Py.Repr((string?)envelope["detail"]!["bucket"]!["name"])}");
        Ensure(
            (string?)envelope["detail"]!["object"]!["etag"] == expectedEtag,
            $"expected detail.object.etag to be the ETag PutObject returned ({Py.Repr(expectedEtag)}), got {Py.Repr((string?)envelope["detail"]!["object"]!["etag"])}");
        Ensure(
            (string?)envelope["detail"]!["reason"] == "PutObject",
            $"an object created by PutObject must carry reason 'PutObject', got {Py.Repr((string?)envelope["detail"]!["reason"])}");

        // The object really is in the archive class, or "restore" below would be meaningless.
        var head = await s3.GetObjectMetadataAsync(bucket, key);
        Ensure(
            head.StorageClass?.Value == "GLACIER",
            $"the object must be GLACIER for a restore to be possible; HeadObject reports {Py.Repr(head.StorageClass?.Value)}");

        // --- leg 2: Object Restore Initiated ------------------------------------------------------
        var restoreStarted = FidelityTime.StartNew();
        await s3.RestoreObjectAsync(new RestoreObjectRequest { BucketName = bucket, Key = key, Days = 1, Tier = GlacierJobTier.Expedited });
        // A second observable that the restore started, so an empty restore queue is not blamed on it.
        // botocore's Restore is the raw x-amz-restore header; the .NET SDK parses its ongoing-request.
        var ongoing = await Poll(
            ctx,
            async () => (await s3.GetObjectMetadataAsync(bucket, key)).RestoreInProgress == true ? "ongoing-request=\"true\"" : null,
            r => r is not null && r.Contains("ongoing-request=\"true\"", StringComparison.Ordinal),
            timeoutS: 60.0,
            what: $"HeadObject reporting an in-flight restore for {Py.Repr(key)}");
        Ensure(ongoing!.Contains("ongoing-request=\"true\"", StringComparison.Ordinal), $"HeadObject must report an in-flight restore, got Restore={Py.Repr(ongoing)}");

        var restored = await Collect(sqs, queues[RestoreInitiated], key, DeliveryBudgetS);
        var restoreLatencyS = restoreStarted.Elapsed.TotalSeconds;
        Ensure(
            restored.Count > 0,
            $"RestoreObject delivered no {Py.Repr(RestoreInitiated)} envelope for {Py.Repr(key)} within " +
            $"{DeliveryBudgetS}s, even though HeadObject reports the restore in flight ({Py.Repr(ongoing)}) and the identical wiring already delivered {Py.Repr(Created)} in " +
            $"{createdLatencyS:F1}s");
        Ensure((string?)restored[0]["detail-type"] == RestoreInitiated, $"expected detail-type {Py.Repr(RestoreInitiated)}, got {Py.Repr((string?)restored[0]["detail-type"])}");
        Ensure((string?)restored[0]["source"] == "aws.s3", $"expected source 'aws.s3', got {Py.Repr((string?)restored[0]["source"])}");
        Ensure(
            (string?)restored[0]["detail"]!["bucket"]!["name"] == bucket,
            $"expected detail.bucket.name {Py.Repr(bucket)}, got {Py.Repr((string?)restored[0]["detail"]!["bucket"]!["name"])}");

        // --- leg 3: Object Tags Added -------------------------------------------------------------
        var tagStarted = FidelityTime.StartNew();
        await s3.PutObjectTaggingAsync(new PutObjectTaggingRequest
        {
            BucketName = bucket,
            Key = key,
            Tagging = new Tagging { TagSet = [new Amazon.S3.Model.Tag { Key = "sim2real", Value = "tagged" }] },
        });
        var tagged = await Collect(sqs, queues[TagsAdded], key, DeliveryBudgetS);
        var tagLatencyS = tagStarted.Elapsed.TotalSeconds;
        Ensure(
            tagged.Count > 0,
            $"PutObjectTagging delivered no {Py.Repr(TagsAdded)} envelope for {Py.Repr(key)} within " +
            $"{DeliveryBudgetS}s; the tag is set (GetObjectTagging is the independent read below) " +
            $"and the identical wiring already delivered {Py.Repr(Created)} and {Py.Repr(RestoreInitiated)}");
        Ensure((string?)tagged[0]["detail-type"] == TagsAdded, $"expected detail-type {Py.Repr(TagsAdded)}, got {Py.Repr((string?)tagged[0]["detail-type"])}");
        var tagset = (await s3.GetObjectTaggingAsync(new GetObjectTaggingRequest { BucketName = bucket, Key = key })).Tagging;
        Ensure(
            tagset.Count == 1 && tagset[0].Key == "sim2real" && tagset[0].Value == "tagged",
            $"the tag that produced the event must be readable at S3; GetObjectTagging reports {Py.Repr(tagset.Select(t => (t.Key, t.Value)).ToList())}");

        // --- the mutual absences: each single-type rule received ONLY its own type ----------------
        var measured = Math.Max(createdLatencyS, Math.Max(restoreLatencyS, tagLatencyS));
        Ensure(
            measured <= MaxCredibleDeliveryWaitS,
            $"the slowest positive control took {measured:F1}s, above the {MaxCredibleDeliveryWaitS}s this check will calibrate an absence against; refusing to " +
            "report an absence rather than widening the window silently");
        var absenceS = Math.Min(Math.Max(AbsenceFloorBudgetS, 3.0 * measured), AbsenceCeilingBudgetS);
        var perQueueS = absenceS / Types.Length;
        var strays = new Dictionary<string, List<string?>>(StringComparer.Ordinal);
        foreach (var detailType in Types)
        {
            var extra = await CollectAll(sqs, queues[detailType], key, perQueueS);
            var wrong = extra.Select(e => (string?)e["detail-type"]).Where(t => t != detailType).ToList();
            if (wrong.Count > 0)
            {
                strays[detailType] = wrong;
            }
        }

        Ensure(
            strays.Count == 0,
            $"each rule matches exactly one detail-type, so no queue may receive another; over {absenceS:F0}s the strays were {Py.Repr(strays)} " +
            "(all three types were delivered in this run, so every queue's wiring is proven)");
        var nonempty = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var detailType in Types)
        {
            var depth = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = queues[detailType],
                AttributeNames = ["ApproximateNumberOfMessages", "ApproximateNumberOfMessagesNotVisible"],
            })).Attributes;
            if (!(depth.Count == 2 && depth.GetValueOrDefault("ApproximateNumberOfMessages") == "0" && depth.GetValueOrDefault("ApproximateNumberOfMessagesNotVisible") == "0"))
            {
                nonempty[detailType] = depth;
            }
        }

        Ensure(nonempty.Count == 0, $"every single-type queue must be empty once its own event has been consumed; these are not: {Py.Repr(nonempty)}");
    }

    private static async Task<List<JsonObject>> Receive(AmazonSQSClient sqs, string url, int wait = 5)
    {
        var msgs = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = wait })).Messages ?? [];
        if (msgs.Count > 0)
        {
            await sqs.DeleteMessageBatchAsync(new DeleteMessageBatchRequest
            {
                QueueUrl = url,
                Entries = [.. msgs.Select((m, i) => new DeleteMessageBatchRequestEntry { Id = $"d{i}", ReceiptHandle = m.ReceiptHandle })],
            });
        }

        return [.. msgs.Select(m => JsonNode.Parse(m.Body)!.AsObject())];
    }

    /// <summary>
    /// A queue whose policy admits default-bus rules; S3 service events land on the DEFAULT bus,
    /// whose rule ARNs carry no bus segment.
    /// </summary>
    private static async Task<(string Url, string Arn)> Queue(AmazonSQSClient sqs, FidelityContext ctx, string region, string account)
    {
        var url = (await sqs.CreateQueueAsync(FidelityContext.UniqueName("sim2real-behav-s3dt-"))).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes["QueueArn"];
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = url,
            Attributes = new()
            {
                ["Policy"] = new JsonObject
                {
                    ["Version"] = "2012-10-17",
                    ["Statement"] = new JsonArray(new JsonObject
                    {
                        ["Sid"] = "AllowEventBridge",
                        ["Effect"] = "Allow",
                        ["Principal"] = new JsonObject { ["Service"] = "events.amazonaws.com" },
                        ["Action"] = "sqs:SendMessage",
                        ["Resource"] = arn,
                        ["Condition"] = new JsonObject { ["ArnLike"] = new JsonObject { ["aws:SourceArn"] = $"arn:aws:events:{region}:{account}:rule/*" } },
                    }),
                }.ToJsonString(),
            },
        });
        return (url, arn);
    }

    /// <summary>
    /// Every envelope on <paramref name="url"/> naming object <paramref name="key"/>, within
    /// <paramref name="budgetS"/>. Returns as soon as one arrives; the caller decides whether one is enough.
    /// </summary>
    private static async Task<List<JsonObject>> Collect(AmazonSQSClient sqs, string url, string key, double budgetS)
    {
        var got = new List<JsonObject>();
        var started = FidelityTime.StartNew();
        while (started.Elapsed.TotalSeconds < budgetS && got.Count == 0)
        {
            got.AddRange((await Receive(sqs, url)).Where(e => (string?)e["detail"]!["object"]!["key"] == key));
        }

        return got;
    }

    /// <summary>
    /// Every envelope naming <paramref name="key"/> that arrives over the WHOLE of
    /// <paramref name="budgetS"/> — the absence form, which must not short-circuit.
    /// </summary>
    private static async Task<List<JsonObject>> CollectAll(AmazonSQSClient sqs, string url, string key, double budgetS)
    {
        var got = new List<JsonObject>();
        var started = FidelityTime.StartNew();
        while (started.Elapsed.TotalSeconds < budgetS)
        {
            got.AddRange((await Receive(sqs, url, wait: 5)).Where(e => (string?)e["detail"]!["object"]!["key"] == key));
        }

        return got;
    }

    private static async Task<string?> Poll(FidelityContext ctx, Func<Task<string?>> probe, Func<string?, bool> predicate, double timeoutS, double intervalS = 5.0, string what = "")
    {
        var started = FidelityTime.StartNew();
        while (true)
        {
            var last = await probe();
            if (predicate(last))
            {
                return last;
            }

            if (started.Elapsed.TotalSeconds >= timeoutS)
            {
                throw Fail($"{(what.Length > 0 ? what : "condition")} not met within {timeoutS}s (last observed: {Py.Repr(last)})");
            }

            await ctx.Delay(TimeSpan.FromSeconds(intervalS));
        }
    }
}

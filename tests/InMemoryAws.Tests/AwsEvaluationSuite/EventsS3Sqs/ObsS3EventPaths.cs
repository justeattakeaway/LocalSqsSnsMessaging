using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;
using EventBridgeTag = Amazon.EventBridge.Model.Tag;
using S3Tag = Amazon.S3.Model.Tag;

namespace InMemoryAws.Tests.AwsEvaluationSuite.EventsS3Sqs;

/// <summary>
/// The helpers the <c>obs-s3-3-*</c> checks share, identical in each upstream file: a bucket wired
/// to both S3 notification paths at once (classic <c>QueueConfigurations</c> and EventBridge), queues
/// that admit exactly one producer, inboxes that keep every body they drain, and the two-round
/// warm-up.
/// </summary>
internal sealed class ObsS3EventPaths(FidelityContext ctx, string check)
{
    public const double DeliveryBudgetS = 90.0;
    public const double NotifyConfigWaitS = 60.0;
    public const double WarmBudgetS = 150.0;
    public const double WarmRoundWaitS = 20.0;
    public const double AbsenceFloorBudgetS = 20.0;
    public const double AbsenceCeilingBudgetS = 60.0;

    public FidelityContext Ctx => ctx;

    /// <summary>Delete every version and delete marker, then the bucket. Never raises a teardown error.</summary>
    public static async Task PurgeBucket(IAmazonS3 s3, string name)
    {
        for (var round = 0; round < 3; round++)
        {
            var doomed = new List<KeyVersion>();
            var versions = await s3.ListVersionsAsync(new ListVersionsRequest { BucketName = name });
            foreach (var v in versions.Versions ?? [])
            {
                doomed.Add(new KeyVersion { Key = v.Key, VersionId = v.VersionId });
            }

            for (var i = 0; i < doomed.Count; i += 1000)
            {
                var resp = await s3.DeleteObjectsAsync(new DeleteObjectsRequest
                {
                    BucketName = name,
                    Objects = doomed.Skip(i).Take(1000).ToList(),
                    Quiet = true,
                });
                if (resp.DeleteErrors is { Count: > 0 } failed)
                {
                    throw new InvalidOperationException($"DeleteObjects left {failed.Count} object(s)");
                }
            }

            try
            {
                await s3.DeleteBucketAsync(name);
                return;
            }
            catch (AmazonS3Exception e) when (e.ErrorCode == "BucketNotEmpty")
            {
            }
        }

        throw new InvalidOperationException($"s3 bucket {name} still not empty after 3 purge rounds");
    }

    public async Task<string> Bucket(AmazonS3Client s3, bool versioned = false)
    {
        var name = FidelityContext.UniqueName("sim2real-xsvc-s3ev-");
        var request = new PutBucketRequest { BucketName = name, UseClientRegion = false };
        if (ctx.Region != "us-east-1")
        {
            request.PutBucketConfiguration = new PutBucketConfiguration { LocationConstraint = new BucketLocationConstraint(ctx.Region) };
        }

        await s3.PutBucketAsync(request);
        ctx.OnCleanup(() => PurgeBucket(s3, name));
        await s3.PutBucketTaggingAsync(new PutBucketTaggingRequest { BucketName = name, TagSet = [new S3Tag { Key = "sim2real", Value = check }] });
        if (versioned)
        {
            await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
            {
                BucketName = name,
                VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled },
            });
        }

        return name;
    }

    public async Task<(string Url, string Arn)> Queue(AmazonSQSClient sqs)
    {
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-xsvc-s3ev-"),
            Tags = new() { ["sim2real"] = check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes["QueueArn"];
        return (url, arn);
    }

    /// <summary>Admit exactly one producer: <paramref name="principal"/> acting for <paramref name="sourceArn"/> in <paramref name="account"/>.</summary>
    public static Task Allow(AmazonSQSClient sqs, string url, string arn, string principal, string sourceArn, string account) =>
        sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = url,
            Attributes = new()
            {
                ["Policy"] = new JsonObject
                {
                    ["Version"] = "2012-10-17",
                    ["Statement"] = new JsonArray(new JsonObject
                    {
                        ["Sid"] = "OneProducer",
                        ["Effect"] = "Allow",
                        ["Principal"] = new JsonObject { ["Service"] = principal },
                        ["Action"] = "sqs:SendMessage",
                        ["Resource"] = arn,
                        ["Condition"] = new JsonObject
                        {
                            ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = sourceArn },
                            ["StringEquals"] = new JsonObject { ["aws:SourceAccount"] = account },
                        },
                    }),
                }.ToJsonString(),
            },
        });

    /// <summary>A default-bus rule with <paramref name="pattern"/>, targeting a fresh queue only that rule may write.</summary>
    public async Task<(string Url, string Rule)> RuleToQueue(AmazonEventBridgeClient ev, AmazonSQSClient sqs, JsonObject pattern, string account)
    {
        var (url, qarn) = await Queue(sqs);
        var rule = FidelityContext.UniqueName("sim2real-xsvc-s3ev-");
        var ruleArn = (await ev.PutRuleAsync(new PutRuleRequest
        {
            Name = rule,
            EventPattern = pattern.ToJsonString(),
            State = RuleState.ENABLED,
            Tags = [new EventBridgeTag { Key = "sim2real", Value = check }],
        })).RuleArn;
        ctx.OnCleanup(() => ev.DeleteRuleAsync(new DeleteRuleRequest { Name = rule }));
        await Allow(sqs, url, qarn, "events.amazonaws.com", ruleArn, account);
        var resp = await ev.PutTargetsAsync(new PutTargetsRequest { Rule = rule, Targets = [new Target { Id = "q", Arn = qarn }] });
        ctx.OnCleanup(() => ev.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = rule, Ids = ["q"] }));
        Ensure(resp.FailedEntryCount == 0, $"put_targets on {Py.Repr(rule)} reported failures: {Py.Repr(resp.FailedEntries?.Select(f => f.ErrorMessage).ToList())}");
        return (url, rule);
    }

    /// <summary>
    /// Classic QueueConfigurations AND EventBridge delivery on the SAME bucket, in one call. S3
    /// validates each queue by sending s3:TestEvent, which fails with InvalidArgument until the
    /// queue policy has propagated; that is the only code retried.
    /// </summary>
    public async Task WireBucket(AmazonS3Client s3, string bucket, List<(string QueueArn, string[] Events)> queueConfigs)
    {
        var started = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(() => s3.PutBucketNotificationAsync(new PutBucketNotificationRequest
            {
                BucketName = bucket,
                QueueConfigurations = [.. queueConfigs.Select(q => new QueueConfiguration
                {
                    Queue = q.QueueArn,
                    Events = [.. q.Events.Select(e => new EventType(e))],
                })],
                EventBridgeConfiguration = new EventBridgeConfiguration(),
            }));
            if (error is null)
            {
                break;
            }

            if (error.Code != "InvalidArgument" || started.Elapsed.TotalSeconds >= NotifyConfigWaitS)
            {
                throw error.Exception;
            }

            await ctx.Delay(TimeSpan.FromSeconds(5));
        }

        var got = await s3.GetBucketNotificationAsync(bucket);
        Ensure(
            got.EventBridgeConfiguration is not null,
            $"bucket {Py.Repr(bucket)} must be EventBridge-enabled; GetBucketNotificationConfiguration returned {Describe(got)}");
        var want = queueConfigs
            .Select(q => $"({q.QueueArn}, ({string.Join(", ", q.Events.Order(StringComparer.Ordinal))}))")
            .Order(StringComparer.Ordinal).ToList();
        var have = (got.QueueConfigurations ?? [])
            .Select(q => $"({q.Queue}, ({string.Join(", ", (q.Events ?? []).Select(e => e.Value).Order(StringComparer.Ordinal))}))")
            .Order(StringComparer.Ordinal).ToList();
        Ensure(have.SequenceEqual(want), $"queue configurations read back as {Py.Repr(have)}, expected {Py.Repr(want)}");
    }

    private static string Describe(GetBucketNotificationResponse got) =>
        $"{{QueueConfigurations: {got.QueueConfigurations?.Count ?? 0}, TopicConfigurations: {got.TopicConfigurations?.Count ?? 0}, EventBridgeConfiguration: {got.EventBridgeConfiguration is not null}}}";

    /// <summary>The absence window: 3x the slowest MEASURED positive delivery in this run, floored and capped.</summary>
    public static double AbsenceWindow(double latencyS) => Math.Min(Math.Max(AbsenceFloorBudgetS, 3.0 * latencyS), AbsenceCeilingBudgetS);

    /// <summary>Classic S3 notification records for <paramref name="key"/> (optionally only eventName == <paramref name="name"/>).</summary>
    public static Func<List<JsonObject>, List<JsonObject>> Classic(string key, string? name = null) =>
        seen => [.. seen
            .SelectMany(b => b["Records"] is JsonArray records ? records.OfType<JsonObject>() : [])
            .Where(r => (string?)r["s3"]?["object"]?["key"] == key && (name is null || (string?)r["eventName"] == name))];

    /// <summary>EventBridge envelopes from aws.s3 for <paramref name="key"/> (optionally only one detail-type).</summary>
    public static Func<List<JsonObject>, List<JsonObject>> Bridge(string key, string? detailType = null) =>
        seen => [.. seen.Where(b => (string?)b["source"] == "aws.s3"
            && (string?)b["detail"]?["object"]?["key"] == key
            && (detailType is null || (string?)b["detail-type"] == detailType))];

    public async Task<List<JsonObject>> Await(Inbox inbox, Func<List<JsonObject>, List<JsonObject>> select, string what, double budgetS = DeliveryBudgetS)
    {
        var started = FidelityTime.StartNew();
        while (true)
        {
            var hits = select(inbox.Seen);
            if (hits.Count > 0)
            {
                return hits;
            }

            if (started.Elapsed.TotalSeconds >= budgetS)
            {
                throw Fail($"{what}: nothing arrived on the {inbox.Label} queue within {budgetS:F0}s; it received {Py.Repr(inbox.Digest())}");
            }

            await inbox.Pump();
        }
    }

    /// <summary>
    /// New rules and fresh queue policies are eventually consistent, so the routes count as live
    /// only once TWO CONSECUTIVE rounds of <c>fire(i)</c> (each on its own warm-up key, never the
    /// key under test) reach EVERY probe within <see cref="WarmRoundWaitS"/>.
    /// </summary>
    public static async Task WarmUp(Func<int, Task<string>> fire, List<(Inbox Inbox, Func<string, Func<List<JsonObject>, List<JsonObject>>> Selector)> probes)
    {
        var started = FidelityTime.StartNew();
        var (streak, i) = (0, 0);
        while (streak < 2)
        {
            if (started.Elapsed.TotalSeconds >= WarmBudgetS)
            {
                throw Fail(
                    $"routes never delivered two consecutive warm-up rounds within {WarmBudgetS:F0}s; " +
                    $"inboxes held {Py.Repr(probes.Select(p => (p.Inbox.Label, p.Inbox.Digest())).ToList())}");
            }

            var key = await fire(i);
            i++;
            var round = FidelityTime.StartNew();
            var pending = probes.ToList();
            while (pending.Count > 0 && round.Elapsed.TotalSeconds < WarmRoundWaitS)
            {
                pending = [.. pending.Where(p => p.Selector(key)(p.Inbox.Seen).Count == 0)];
                foreach (var (ib, _) in pending)
                {
                    await ib.Pump(wait: 1);
                }
            }

            streak = pending.Count == 0 ? streak + 1 : 0;
        }
    }

    /// <summary>
    /// Everything one queue has delivered so far. Messages are deleted as they are read, and every
    /// body is kept, so a later assertion can inspect strays as well as hits.
    /// </summary>
    internal sealed class Inbox(AmazonSQSClient sqs, string url, string label)
    {
        public string Label => label;

        public List<JsonObject> Seen { get; } = [];

        public async Task Pump(int wait = 2)
        {
            var msgs = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = wait,
            })).Messages ?? [];
            if (msgs.Count > 0)
            {
                await sqs.DeleteMessageBatchAsync(new DeleteMessageBatchRequest
                {
                    QueueUrl = url,
                    Entries = [.. msgs.Select((m, i) => new DeleteMessageBatchRequestEntry { Id = $"m{i}", ReceiptHandle = m.ReceiptHandle })],
                });
            }

            Seen.AddRange(msgs.Select(m => JsonNode.Parse(m.Body)!.AsObject()));
        }

        public async Task Soak(double seconds)
        {
            var started = FidelityTime.StartNew();
            while (started.Elapsed.TotalSeconds < seconds)
            {
                await Pump();
            }
        }

        public List<string> Digest()
        {
            var output = new List<string>();
            foreach (var b in Seen)
            {
                if (b["Records"] is JsonArray records)
                {
                    output.AddRange(records.OfType<JsonObject>().Select(r => $"{(string?)r["eventName"]}:{(string?)r["s3"]?["object"]?["key"]}"));
                }
                else if (b.ContainsKey("detail-type"))
                {
                    output.Add($"{(string?)b["detail-type"]}:{(string?)b["detail"]?["object"]?["key"]}");
                }
                else
                {
                    var text = b["Event"]?.ToJsonString() ?? b.ToJsonString();
                    output.Add(text.Length > 60 ? text[..60] : text);
                }
            }

            return output;
        }
    }
}

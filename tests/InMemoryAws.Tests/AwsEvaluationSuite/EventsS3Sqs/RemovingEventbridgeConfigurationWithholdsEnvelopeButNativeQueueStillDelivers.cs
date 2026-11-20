// Translated from behavioral/events__s3__sqs/check_removing_eventbridge_configuration_withholds_envelope_but_native_queue_still_delivers.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.EventBridge.Model;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;
using EventsTag = Amazon.EventBridge.Model.Tag;
using S3Tag = Amazon.S3.Model.Tag;

namespace InMemoryAws.Tests.AwsEvaluationSuite.EventsS3Sqs;

/// <summary>events__s3__sqs.removing-eventbridge-configuration-withholds-envelope-but-native-queue-still-delivers</summary>
[Category("AwsEvaluationSuite")]
public sealed class RemovingEventbridgeConfigurationWithholdsEnvelopeButNativeQueueStillDelivers
{
    private const string Check = "removing-eventbridge-configuration-withholds-envelope-but-native-queue-still-delivers";

    /// <summary>
    /// A key exercising every encoding rule the two paths disagree on. The native S3 record carries it
    /// form-encoded (space -&gt; '+', '+' -&gt; %2B, '=' -&gt; %3D, UTF-8 percent-escaped, '~' -&gt; %7E, '/' kept);
    /// the EventBridge envelope carries it raw.
    /// </summary>
    private const string Key = "dual path/report 2026+q3=é~.txt";
    private const string KeyNative = "dual+path/report+2026%2Bq3%3D%C3%A9%7E.txt";
    private const string DualId = "dual-native";
    private const string NativeOnlyId = "native-only";

    /// <summary>
    /// Real AWS routes by a cached copy of the configuration loaded at the bucket's first event, for
    /// roughly five minutes (measured 294 s). The flip wait is bounded generously above that.
    /// </summary>
    private const double FlipTimeoutS = 480.0;

    private const string XsvcPrefix = "sim2real-xsvc-";
    private const string ManagedTagKey = "sim2real:managed";

    private static readonly byte[] BodyBytes = Encoding.UTF8.GetBytes("dual-path-subject");

    /// <summary>
    /// One PutObject yields one native S3 record and one EventBridge envelope (raw vs form-encoded
    /// key); after the EventBridge configuration is removed, writes routed by the new configuration
    /// still reach the native queue and never produce an envelope.
    /// </summary>
    [Test]
    [DisplayName("events__s3__sqs.removing-eventbridge-configuration-withholds-envelope-but-native-queue-still-delivers")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        // timing:real-time — ttl: 300 s
        await using var ctx = new FidelityContext(FidelityContext.RealTimeBudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var events = ctx.Events;
        var bucket = await CreateBucket(ctx);
        var qNative = await CreateQueue(ctx);
        var qEvents = await CreateQueue(ctx);
        await SetQueuePolicy(ctx, qNative, "s3.amazonaws.com", bucket.Arn);

        var rule = XName("rule");
        var ruleArn = (await events.PutRuleAsync(new PutRuleRequest
        {
            Name = rule,
            EventPattern = new JsonObject
            {
                ["source"] = new JsonArray("aws.s3"),
                ["detail-type"] = new JsonArray("Object Created"),
                ["detail"] = new JsonObject { ["bucket"] = new JsonObject { ["name"] = new JsonArray(bucket.Name) } },
            }.ToJsonString(),
            State = Amazon.EventBridge.RuleState.ENABLED,
            Tags = [.. XsvcTagsKv().Select(t => new EventsTag { Key = t.Key, Value = t.Value })],
        })).RuleArn;
        ctx.OnCleanup(() => events.DeleteRuleAsync(new DeleteRuleRequest { Name = rule, Force = true }));
        await SetQueuePolicy(ctx, qEvents, "events.amazonaws.com", ruleArn);
        var resp = await events.PutTargetsAsync(new PutTargetsRequest { Rule = rule, Targets = [new Target { Id = "queue-e", Arn = qEvents.Arn }] });
        ctx.OnCleanup(() => events.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = rule, Ids = ["queue-e"], Force = true }));
        Ensure(resp.FailedEntryCount == 0, $"PutTargets reported failures: FailedEntryCount={resp.FailedEntryCount}");

        QueueConfiguration NativeCfg(string id) => new() { Id = id, Queue = qNative.Arn, Events = ["s3:ObjectCreated:*"] };
        await s3.PutBucketNotificationAsync(new PutBucketNotificationRequest
        {
            BucketName = bucket.Name,
            EventBridgeConfiguration = new EventBridgeConfiguration(),
            QueueConfigurations = [NativeCfg(DualId)],
        });
        var got = await s3.GetBucketNotificationAsync(bucket.Name);
        Ensure(
            got.EventBridgeConfiguration is not null,
            "GetBucketNotificationConfiguration omits EventBridgeConfiguration after enabling it");

        var native = new Collector(ctx, qNative.Url);
        var envelopes = new Collector(ctx, qEvents.Url);

        // Warm-up: S3 -> EventBridge delivery can lag the enabling call (observed once at >120 s on a
        // fresh bucket, immediate on others). Write throwaway keys until one reaches E, so the subject
        // write below is not raced against enablement. Native copies of them are ignored by key.
        var warm = new List<string>();
        var clock = FidelityTime.StartNew();
        var warmDeadline = clock.Elapsed.TotalSeconds + 360.0;
        var nextPut = 0.0;
        while (clock.Elapsed.TotalSeconds < warmDeadline && !warm.Any(k => envelopes.EnvelopesFor(k).Count > 0))
        {
            if (clock.Elapsed.TotalSeconds >= nextPut)
            {
                warm.Add($"warmup/{warm.Count:D3}.txt");
                await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket.Name, Key = warm[^1], ContentBody = "warm" });
                nextPut = clock.Elapsed.TotalSeconds + 10.0;
            }

            await envelopes.Poll();
        }

        Ensure(
            warm.Any(k => envelopes.EnvelopesFor(k).Count > 0),
            $"none of {warm.Count} writes over 360s produced an EventBridge envelope on E after EventBridgeConfiguration was enabled; E saw {envelopes.Summary()}");

        // ---------------- Leg 1: one write, two families, two encodings.
        var started = clock.Elapsed.TotalSeconds;
        var put = await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket.Name, Key = Key, InputStream = new MemoryStream(BodyBytes) });
        var etag = put.ETag.Trim('"');
        var deadline = clock.Elapsed.TotalSeconds + 120.0;
        double? eLatency = null;
        while (clock.Elapsed.TotalSeconds < deadline && !(native.NativeFor(KeyNative).Count > 0 && envelopes.EnvelopesFor(Key).Count > 0))
        {
            await native.Poll();
            await envelopes.Poll();
            if (eLatency is null && envelopes.EnvelopesFor(Key).Count > 0)
            {
                eLatency = clock.Elapsed.TotalSeconds - started;
            }
        }

        Ensure(
            native.NativeFor(KeyNative).Count > 0,
            $"no native record for {Py.Repr(KeyNative)} reached queue N within 120s; seen: {native.Summary()}");
        Ensure(
            envelopes.EnvelopesFor(Key).Count > 0,
            $"no EventBridge envelope for raw key {Py.Repr(Key)} reached queue E within 120s; seen: {envelopes.Summary()}");

        // Settle: keep draining briefly so a second (wrong) event on either path is caught.
        var settle = clock.Elapsed.TotalSeconds + 20.0;
        while (clock.Elapsed.TotalSeconds < settle)
        {
            await native.Poll();
            await envelopes.Poll();
        }

        var recs = native.NativeFor(KeyNative);
        var recSequencers = recs.Select(r => (string?)r["s3"]?["object"]?["sequencer"]).ToHashSet();
        Ensure(
            recSequencers.Count == 1,
            $"queue N received {recs.Count} records for one PutObject with distinct sequencers {Py.Repr(recSequencers.Order().ToList())}; expected one event");
        var rec = recs[0];
        Ensure(
            (string?)rec["eventName"] == "ObjectCreated:Put",
            $"native record eventName is {Py.Repr((string?)rec["eventName"])}, expected 'ObjectCreated:Put'");
        Ensure(
            (string?)rec["s3"]?["configurationId"] == DualId,
            $"native record configurationId is {Py.Repr((string?)rec["s3"]?["configurationId"])}, expected {Py.Repr(DualId)}");
        Ensure(
            (string?)rec["s3"]?["object"]?["key"] == KeyNative,
            $"native record key is {Py.Repr((string?)rec["s3"]?["object"]?["key"])}; S3 records carry the key form-encoded, expected {Py.Repr(KeyNative)} for raw {Py.Repr(Key)}");

        var envs = envelopes.EnvelopesFor(Key);
        var envSequencers = envs.Select(e => (string?)e["detail"]?["object"]?["sequencer"]).ToHashSet();
        Ensure(
            envSequencers.Count == 1,
            $"queue E received {envs.Count} envelopes for one PutObject with distinct sequencers {Py.Repr(envSequencers.Order().ToList())}; expected one event");
        var env = envs[0];
        Ensure((string?)env["source"] == "aws.s3", $"envelope source is {Py.Repr((string?)env["source"])}, expected 'aws.s3'");
        Ensure(
            (string?)env["detail-type"] == "Object Created",
            $"envelope detail-type is {Py.Repr((string?)env["detail-type"])}, expected 'Object Created'");
        Ensure(
            (string?)env["detail"]?["bucket"]?["name"] == bucket.Name,
            $"envelope names bucket {Py.Repr((string?)env["detail"]?["bucket"]?["name"])}, expected {Py.Repr(bucket.Name)}");
        Ensure(
            (string?)env["detail"]?["object"]?["key"] == Key,
            $"envelope key is {Py.Repr((string?)env["detail"]?["object"]?["key"])}; EventBridge carries the RAW key {Py.Repr(Key)}");

        // Same write, two spellings: native records say `eTag`, EventBridge detail says `etag`.
        foreach (var (nativeField, envField, want) in new (string, string, JsonNode)[] { ("size", "size", BodyBytes.Length), ("eTag", "etag", etag) })
        {
            var gotNative = rec["s3"]?["object"]?[nativeField];
            var gotEnv = env["detail"]?["object"]?[envField];
            Ensure(
                JsonNode.DeepEquals(gotNative, want) && JsonNode.DeepEquals(gotEnv, want),
                $"the two paths disagree with the write: native {nativeField}={gotNative?.ToJsonString()}, envelope {envField}={gotEnv?.ToJsonString()}, PutObject {want.ToJsonString()}");
        }

        Ensure(
            (string?)rec["s3"]?["object"]?["sequencer"] == (string?)env["detail"]?["object"]?["sequencer"],
            $"one write carries two sequencers: native {Py.Repr((string?)rec["s3"]?["object"]?["sequencer"])}, envelope {Py.Repr((string?)env["detail"]?["object"]?["sequencer"])}");
        Ensure(
            (string?)env["detail"]?["reason"] == "PutObject",
            $"envelope detail.reason is {Py.Repr((string?)env["detail"]?["reason"])}, expected 'PutObject'");
        var expected = new HashSet<string?>(warm, StringComparer.Ordinal) { Key };
        var strays = StrayKeys(envelopes, expected);
        Ensure(strays.Count == 0, $"queue E received envelopes for keys this check never wrote: {Py.Repr(strays)}");

        // ---------------- Leg 2: drop EventBridgeConfiguration, keep the native queue.
        await s3.PutBucketNotificationAsync(new PutBucketNotificationRequest
        {
            BucketName = bucket.Name,
            QueueConfigurations = [NativeCfg(NativeOnlyId)],
        });
        got = await s3.GetBucketNotificationAsync(bucket.Name);
        Ensure(
            got.EventBridgeConfiguration is null,
            "GetBucketNotificationConfiguration still reports EventBridgeConfiguration after a replace that omitted it");
        var gotIds = (got.QueueConfigurations ?? []).Select(c => c.Id).ToList();
        Ensure(
            gotIds.SequenceEqual([NativeOnlyId]),
            $"the replaced document reads back queue configurations {Py.Repr(gotIds)}, expected [{Py.Repr(NativeOnlyId)}]");

        var written = new List<string>();
        var flipDeadline = clock.Elapsed.TotalSeconds + FlipTimeoutS;
        nextPut = 0.0;
        while (clock.Elapsed.TotalSeconds < flipDeadline && RoutedBy(native, written, NativeOnlyId).Count == 0)
        {
            if (clock.Elapsed.TotalSeconds >= nextPut)
            {
                var key = $"after/{written.Count:D3}.txt";
                await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket.Name, Key = key, InputStream = new MemoryStream(BodyBytes) });
                written.Add(key);
                nextPut = clock.Elapsed.TotalSeconds + 8.0;
            }

            await native.Poll();
            await envelopes.Poll();
        }

        var flipped = RoutedBy(native, written, NativeOnlyId);
        Ensure(
            flipped.Count > 0,
            $"none of {written.Count} writes over {FlipTimeoutS:F0}s was routed by the replacement configuration {Py.Repr(NativeOnlyId)}; native routing seen: {{{string.Join(", ", written.Select(k => $"{Py.Repr(k)}: {Py.Repr(Ids(native, k))}"))}}}");

        var holdS = Math.Min(Math.Max(3.0 * (eLatency is { } latency && latency != 0 ? latency : 15.0), 15.0), 120.0);
        var holdUntil = clock.Elapsed.TotalSeconds + holdS;
        for (var i = 0; i < 2; i++)
        {
            var key = $"after/{written.Count:D3}.txt";
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket.Name, Key = key, InputStream = new MemoryStream(BodyBytes) });
            written.Add(key);
        }

        while (clock.Elapsed.TotalSeconds < holdUntil)
        {
            await native.Poll();
            await envelopes.Poll();
        }

        var newRouted = RoutedBy(native, written, NativeOnlyId);
        Ensure(
            written[^2..].All(newRouted.Contains),
            $"the two writes made after the flip were not both delivered to N under {Py.Repr(NativeOnlyId)} within {holdS:F0}s: [{string.Join(", ", written[^2..].Select(k => $"({Py.Repr(k)}, {Py.Repr(Ids(native, k))})"))}]");
        var leaked = newRouted.Where(k => envelopes.EnvelopesFor(k).Count > 0).ToList();
        Ensure(
            leaked.Count == 0,
            $"writes routed by the configuration WITHOUT EventBridgeConfiguration still produced EventBridge envelopes on E: {Py.Repr(leaked)} (held {holdS:F0}s after the flip; leg 1 measured E latency {eLatency}s)");
        Ensure(
            newRouted.All(k => native.NativeFor(k).All(r => (string?)r["eventName"] == "ObjectCreated:Put")),
            $"the surviving native path changed eventName: [{string.Join(", ", newRouted.Select(k => $"({Py.Repr(k)}, [{string.Join(", ", native.NativeFor(k).Select(r => (string?)r["eventName"]))}])"))}]");
    }

    private sealed record Queue(string Name, string Url, string Arn);

    private sealed record Bucket(string Name, string Arn);

    private static List<KeyValuePair<string, string>> XsvcTagsKv() =>
        [new(ManagedTagKey, "true"), new("sim2real:check", Check.Length > 200 ? Check[..200] : Check)];

    private static string XName(string kind) => FidelityContext.UniqueName($"{XsvcPrefix}{kind}-");

    private static async Task<Queue> CreateQueue(FidelityContext ctx)
    {
        var sqs = ctx.Sqs;
        var name = XName("q");
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Tags = new() { [ManagedTagKey] = "true" } })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes["QueueArn"];
        return new Queue(name, url, arn);
    }

    private static Task SetQueuePolicy(FidelityContext ctx, Queue queue, string service, string sourceArn)
    {
        var policy = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Sid"] = "AllowServiceSend",
                ["Effect"] = "Allow",
                ["Principal"] = new JsonObject { ["Service"] = service },
                ["Action"] = "sqs:SendMessage",
                ["Resource"] = queue.Arn,
                ["Condition"] = new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = sourceArn } },
            }),
        };
        return ctx.Sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = queue.Url, Attributes = new() { ["Policy"] = policy.ToJsonString() } });
    }

    private static async Task<Bucket> CreateBucket(FidelityContext ctx)
    {
        var s3 = ctx.S3;
        var name = await S3Fidelity.CreateBucket(ctx, $"{XsvcPrefix}bkt-", s3);
        await s3.PutBucketTaggingAsync(new PutBucketTaggingRequest
        {
            BucketName = name,
            TagSet = [.. XsvcTagsKv().Select(t => new S3Tag { Key = t.Key, Value = t.Value })],
        });
        return new Bucket(name, $"arn:aws:s3:::{name}");
    }

    /// <summary>
    /// Accumulates everything a queue delivers: native S3 Records keyed by (encoded) key, and
    /// EventBridge envelopes keyed by detail.object.key. Each poll is one short receive.
    /// </summary>
    private sealed class Collector(FidelityContext ctx, string url)
    {
        private readonly AmazonSQSClient _sqs = ctx.Sqs;

        public Dictionary<string, List<JsonNode>> Records { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, List<JsonNode>> Envelopes { get; } = new(StringComparer.Ordinal);

        public List<string> Other { get; } = [];

        public async Task Poll()
        {
            var resp = await _sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 });
            foreach (var msg in resp.Messages ?? [])
            {
                await _sqs.DeleteMessageAsync(url, msg.ReceiptHandle);
                JsonNode? body;
                try
                {
                    body = JsonNode.Parse(msg.Body ?? string.Empty);
                }
                catch (JsonException)
                {
                    Other.Add(Truncate(msg.Body ?? string.Empty));
                    continue;
                }

                if (body is JsonObject o && o.ContainsKey("Records"))
                {
                    foreach (var r in o["Records"]!.AsArray())
                    {
                        Add(Records, (string?)r?["s3"]?["object"]?["key"], r!);
                    }
                }
                else if (body is JsonObject e && e.ContainsKey("detail-type"))
                {
                    Add(Envelopes, (string?)e["detail"]?["object"]?["key"], e);
                }
                else
                {
                    Other.Add(Truncate(body?.ToJsonString() ?? "None"));
                }
            }
        }

        public List<JsonNode> NativeFor(string key) => Records.GetValueOrDefault(key) ?? [];

        public List<JsonNode> EnvelopesFor(string key) => Envelopes.GetValueOrDefault(key) ?? [];

        public string Summary() =>
            $"{{'records': {Py.Repr(Records.Keys.Order(StringComparer.Ordinal).ToList())}, 'envelopes': {Py.Repr(Envelopes.Keys.Order(StringComparer.Ordinal).ToList())}, 'other': {Py.Repr(Other.Take(3).ToList())}}}";

        private static void Add(Dictionary<string, List<JsonNode>> map, string? key, JsonNode value)
        {
            var k = key ?? "None";
            if (!map.TryGetValue(k, out var list))
            {
                map[k] = list = [];
            }

            list.Add(value);
        }

        private static string Truncate(string value) => value.Length > 200 ? value[..200] : value;
    }

    private static List<string?> Ids(Collector native, string key) =>
        [.. native.NativeFor(key).Select(r => (string?)r["s3"]?["configurationId"])];

    /// <summary>Keys (raw == encoded here: [a-z0-9/._] only) whose native record names config_id.</summary>
    private static List<string> RoutedBy(Collector native, List<string> keys, string configId) =>
        [.. keys.Where(k => Ids(native, k).Contains(configId))];

    private static List<string> StrayKeys(Collector envelopes, HashSet<string?> expected) =>
        [.. envelopes.Envelopes.Keys.Where(k => !expected.Contains(k)).Order(StringComparer.Ordinal)];
}

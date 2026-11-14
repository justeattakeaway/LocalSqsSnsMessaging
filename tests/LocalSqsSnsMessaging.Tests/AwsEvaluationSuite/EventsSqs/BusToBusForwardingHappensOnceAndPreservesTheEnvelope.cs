// Translated from behavioral/events__sqs/check_bus_to_bus_forwarding_happens_once_and_preserves_the_envelope.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.EventBridge.Model;
using Amazon.Runtime;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using EventBridgeTag = Amazon.EventBridge.Model.Tag;
using SqsMessage = Amazon.SQS.Model.Message;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.EventsSqs;

// EventBridge forwards an event between same-Region buses at most once, and the forwarded event is
// identical to the original:
// https://aws.amazon.com/blogs/compute/using-bus-to-bus-event-routing-with-amazon-eventbridge/

/// <summary>events__sqs.bus-to-bus-forwarding-happens-once-and-preserves-the-envelope</summary>
[Category("AwsEvaluationSuite")]
public sealed class BusToBusForwardingHappensOnceAndPreservesTheEnvelope
{
    private const double MinAbsenceWindowS = 45.0;
    private const double LivenessTimeoutS = 120.0;
    private const double DeliveryTimeoutS = 90.0;

    // Fields that differ between any two events by construction and are therefore excluded from the
    // forwarded-versus-direct envelope comparison.
    private static readonly string[] PerEventFields = ["id", "time", "detail"];

    private const string XsvcPrefix = "sim2real-xsvc-";
    private const string ManagedTagKey = "sim2real:managed";

    /// <summary>
    /// A→B forwarding delivers an untouched envelope to B's target, and B does not forward it on to C
    /// even though B→C forwarding demonstrably works.
    /// </summary>
    [Test]
    [DisplayName("events__sqs.bus-to-bus-forwarding-happens-once-and-preserves-the-envelope")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EventBridgeBusTargets, "A rule whose target is another event bus never forwards: the B->C control event never reaches the queue on bus C's rule")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var sqs = ctx.Sqs;
        var account = ctx.Account;
        var source = XName("src");

        var (busA, _) = await CreateBus(ctx);
        var (busB, arnB) = await CreateBus(ctx);
        var (busC, arnC) = await CreateBus(ctx);

        // IAM is not emulated: the role is only an ARN on the forwarding targets.
        var roleArn = CreateEventsRole(ctx);

        var queueB = await CreateQueue(ctx);
        var queueC = await CreateQueue(ctx);

        var pattern = new JsonObject { ["source"] = new JsonArray(source) };
        string[] resources = [$"arn:aws:events:{ctx.Region}:{account}:event-bus/{busA}"];
        // A -> B, and B -> C: the two forwarding hops.
        await PutRule(ctx, busA, pattern, new Target { Id = "t1", Arn = arnB, RoleArn = roleArn });
        await PutRule(ctx, busB, pattern, new Target { Id = "t1", Arn = arnC, RoleArn = roleArn });
        // The two observation points, one per bus.
        var ruleBq = await PutRule(ctx, busB, pattern, new Target { Id = "t1", Arn = queueB.Arn });
        var ruleCq = await PutRule(ctx, busC, pattern, new Target { Id = "t1", Arn = queueC.Arn });
        await SetQueuePolicy(ctx, queueB, "events.amazonaws.com", ruleBq.Arn);
        await SetQueuePolicy(ctx, queueC, "events.amazonaws.com", ruleCq.Arn);

        // --- twin control, leg 1: B -> C -> queue_c really works, measured on this exact wiring. ---
        var (directMarker, directOnC, latencyBc) = await EmitUntilDelivered(
            ctx, busB, source, "Direct", queueC.Url, timeoutS: LivenessTimeoutS);
        Ensure(
            (string?)directOnC["detail"]?["marker"] == directMarker,
            "the B->C control event must arrive on queue_c carrying its own marker, got " +
            $"{directOnC.ToJsonString()}");

        // --- twin control, leg 2: A -> B -> queue_b really works, measured on this exact wiring. ---
        var (_, _, latencyAb) = await EmitUntilDelivered(
            ctx, busA, source, "Warmup", queueB.Url, timeoutS: LivenessTimeoutS);

        // Both hops are now live. Clear both queues before establishing the reference envelope.
        await Drain(ctx, queueB.Url, windowS: 5.0);
        await Drain(ctx, queueC.Url, windowS: 5.0);

        // --- the reference envelope: put DIRECTLY on bus B (never forwarded), with the SAME
        // detail-type and resources the measured event below will carry. The twin controls above use
        // their own detail-types ("Direct"/"Warmup") and empty Resources to prove liveness, so their
        // copies are not fit to serve as the reference for the field-by-field comparison: comparing
        // against them would flag detail-type/resources as "rewritten by forwarding" when they were
        // simply given different literals at PutEvents time. This reference isolates what forwarding
        // itself does.
        var referenceMarker = Guid.NewGuid().ToString("N");
        var refResp = await events.PutEventsAsync(new PutEventsRequest
        {
            Entries =
            [
                new PutEventsRequestEntry
                {
                    Source = source,
                    DetailType = "Forwarded",
                    Detail = $$$"""{"marker": "{{{referenceMarker}}}", "nested": {"n": 7, "flag": true}}""",
                    Resources = [.. resources],
                    EventBusName = busB,
                },
            ],
        });
        Ensure(
            refResp.FailedEntryCount == 0,
            $"the reference PutEvents on bus {busB} must be accepted, got {Repr(refResp)}");
        var directOnB = (await AwaitMarker(ctx, queueB.Url, referenceMarker, timeoutS: DeliveryTimeoutS)).Body;

        // Clear both queues again so the measured event is the only thing in flight.
        await Drain(ctx, queueB.Url, windowS: 5.0);
        await Drain(ctx, queueC.Url, windowS: 5.0);

        // --- the measured event: emitted ONCE on A. ---
        var marker = Guid.NewGuid().ToString("N");
        var detailJson = $$$"""{"marker": "{{{marker}}}", "nested": {"n": 7, "flag": true}}""";
        var detail = JsonNode.Parse(detailJson);
        var resp = await events.PutEventsAsync(new PutEventsRequest
        {
            Entries =
            [
                new PutEventsRequestEntry
                {
                    Source = source,
                    DetailType = "Forwarded",
                    Detail = detailJson,
                    Resources = [.. resources],
                    EventBusName = busA,
                },
            ],
        });
        Ensure(
            resp.FailedEntryCount == 0,
            $"the measured PutEvents on bus {busA} must be accepted, got {Repr(resp)}");

        // --- first hop: the forwarded envelope arrives at the CONSUMER, and is untouched. ---
        var (forwarded, _) = await AwaitMarker(ctx, queueB.Url, marker, timeoutS: DeliveryTimeoutS);
        Ensure(
            (string?)forwarded["source"] == source,
            $"the forwarded event must keep its source: expected '{source}', got {Repr(forwarded["source"])}");
        Ensure(
            (string?)forwarded["detail-type"] == "Forwarded",
            "the forwarded event must keep its detail-type: expected 'Forwarded', got " +
            $"{Repr(forwarded["detail-type"])}");
        Ensure(
            JsonNode.DeepEquals(forwarded["detail"], detail),
            $"the forwarded event must keep its detail byte-for-byte: expected {detail!.ToJsonString()}, got " +
            $"{Repr(forwarded["detail"])}");
        Ensure(
            JsonNode.DeepEquals(forwarded["resources"], new JsonArray([.. resources.Select(r => (JsonNode?)r)])),
            $"the forwarded event must keep its resources array: expected ['{string.Join("', '", resources)}'], got " +
            $"{Repr(forwarded["resources"])}");
        Ensure(
            (string?)forwarded["account"] == account,
            $"the forwarded event must keep the originating account: expected '{account}', got " +
            $"{Repr(forwarded["account"])}");
        Ensure(
            (string?)forwarded["region"] == ctx.Region,
            $"the forwarded event must keep the originating region: expected '{ctx.Region}', got " +
            $"{Repr(forwarded["region"])}");
        Ensure(
            !forwarded.ContainsKey("replay-name"),
            $"a forwarded event is not a replayed event and must not carry replay-name: {forwarded.ToJsonString()}");
        // Structurally: forwarding adds and removes nothing relative to a direct delivery.
        var forwardedKeys = forwarded.Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList();
        var directKeys = directOnB.Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList();
        Ensure(
            forwardedKeys.SequenceEqual(directKeys),
            "forwarding must not add or remove envelope fields: forwarded keys " +
            $"[{string.Join(", ", forwardedKeys.Select(k => $"'{k}'"))}] vs directly-delivered keys [{string.Join(", ", directKeys.Select(k => $"'{k}'"))}]");
        var differing = forwarded
            .Where(kv => !PerEventFields.Contains(kv.Key) && !JsonNode.DeepEquals(directOnB[kv.Key], kv.Value))
            .Select(kv => $"\"{kv.Key}\": [{directOnB[kv.Key]?.ToJsonString() ?? "null"}, {kv.Value?.ToJsonString() ?? "null"}]")
            .ToList();
        if (differing.Count > 0)
        {
            var pairs = "{" + string.Join(", ", differing) + "}";
            throw Fail(
                "forwarding must not rewrite any envelope field: (direct, forwarded) pairs that differ were " +
                $"{pairs[..Math.Min(300, pairs.Length)]}");
        }

        // --- second hop must NOT happen, bounded by the two measured latencies. ---
        var window = Math.Max(MinAbsenceWindowS, 3.0 * (latencyAb + latencyBc));
        var leaked = (await Drain(ctx, queueC.Url, windowS: window))
            .Where(body => body is JsonObject o && (string?)o["detail"]?["marker"] == marker)
            .ToList();
        if (leaked.Count > 0)
        {
            var text = "[" + string.Join(", ", leaked.Select(b => b?.ToJsonString())) + "]";
            throw Fail(
                $"bus {busC} received an event that had ALREADY been forwarded once (A->B): EventBridge " +
                "forwards between same-Region buses at most once, so this is a second hop that must not " +
                $"exist. Window was {window:F0}s = 3x(A->B {latencyAb:F1}s + B->C {latencyBc:F1}s). " +
                $"Leaked: {text[..Math.Min(300, text.Length)]}");
        }

        var depth = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = queueC.Url, AttributeNames = ["ApproximateNumberOfMessages"],
        })).Attributes["ApproximateNumberOfMessages"];
        Ensure(
            depth == "0",
            "second observable disagrees with the empty drain: " +
            $"ApproximateNumberOfMessages on the bus-{busC} queue is '{depth}', expected '0'");

        // --- and the B->C hop is STILL live at the end, so the silence above was about the hop limit
        // rather than about C having quietly died mid-run. ---
        var (recheckMarker, recheckBody, _) = await EmitUntilDelivered(
            ctx, busB, source, "Recheck", queueC.Url, timeoutS: LivenessTimeoutS);
        Ensure(
            (string?)recheckBody["detail"]?["marker"] == recheckMarker,
            $"the B->C wiring must still deliver after the absence window, got {recheckBody.ToJsonString()}");
    }

    private sealed record Queue(string Name, string Url, string Arn);

    private sealed record RuleRef(string Name, string Arn);

    private static string Repr(JsonNode? node) => node is null ? "None" : node.ToJsonString();

    private static string Repr(PutEventsResponse resp) =>
        $"{{'FailedEntryCount': {resp.FailedEntryCount}, 'Entries': [{string.Join(", ", (resp.Entries ?? []).Select(e => $"{{'EventId': '{e.EventId}', 'ErrorCode': '{e.ErrorCode}'}}"))}]}}";

    private static Dictionary<string, string> XsvcResourceTags() =>
        new() { [ManagedTagKey] = "true", ["sim2real:prefix"] = XsvcPrefix.TrimEnd('-') };

    private static List<EventBridgeTag> XsvcTagsKv() =>
        [.. XsvcResourceTags().Select(kv => new EventBridgeTag { Key = kv.Key, Value = kv.Value })];

    /// <summary>A unique, prefix-tagged resource name: sim2real-xsvc-&lt;kind&gt;-&lt;uuid&gt;.</summary>
    private static string XName(string kind) => FidelityContext.UniqueName($"{XsvcPrefix}{kind}-");

    /// <summary>
    /// Run a teardown call. With a leak label, an error code outside <paramref name="tolerated"/> is
    /// reported as a leak; without one, a refused teardown call is ignored.
    /// </summary>
    private static async Task Tolerate(Func<Task> fn, string tolerated, string leak = "")
    {
        try
        {
            await fn();
        }
        catch (AmazonServiceException exc)
        {
            if (leak.Length > 0 && exc.ErrorCode != tolerated)
            {
                throw new InvalidOperationException($"{leak}: {exc.ErrorCode}", exc);
            }
        }
    }

    private static async Task<(string Name, string Arn)> CreateBus(FidelityContext ctx)
    {
        var events = ctx.Events;
        var name = XName("bus");
        var arn = (await events.CreateEventBusAsync(new CreateEventBusRequest { Name = name, Tags = XsvcTagsKv() })).EventBusArn;
        ctx.OnCleanup(() => Tolerate(
            () => ctx.Events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = name }),
            "ResourceNotFoundException", leak: $"could not delete event bus {name}"));
        return (name, arn);
    }

    /// <summary>An IAM role EventBridge can assume, allowed to PutEvents on the given buses.</summary>
    /// <remarks>
    /// IAM is not emulated: <c>iam.create_role</c> becomes <see cref="FidelityContext.RoleArn"/>, and the
    /// PutRolePolicy call and the role's teardown are dropped.
    /// </remarks>
    private static string CreateEventsRole(FidelityContext ctx)
    {
        var name = XName("role");
        return ctx.RoleArn(name[..Math.Min(64, name.Length)]);
    }

    /// <summary>Create an SQS queue (registers delete_queue teardown). Returns {name,url,arn}.</summary>
    private static async Task<Queue> CreateQueue(FidelityContext ctx)
    {
        var sqs = ctx.Sqs;
        var name = XName("q");
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Tags = XsvcResourceTags() })).QueueUrl;
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes["QueueArn"];
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
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
        return ctx.Sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = queue.Url,
            Attributes = new() { ["Policy"] = policy.ToJsonString() },
        });
    }

    /// <summary>A rule on <paramref name="bus"/> for <paramref name="pattern"/> with exactly one target. Registers teardown. Returns {name,arn}.</summary>
    private static async Task<RuleRef> PutRule(FidelityContext ctx, string bus, JsonObject pattern, Target target)
    {
        var events = ctx.Events;
        var name = XName("rule");
        var arn = (await events.PutRuleAsync(new PutRuleRequest
        {
            Name = name, EventBusName = bus, EventPattern = pattern.ToJsonString(), Tags = XsvcTagsKv(),
        })).RuleArn;

        async Task Teardown()
        {
            var ev = ctx.Events;
            await Tolerate(
                () => ev.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = name, EventBusName = bus, Ids = [target.Id] }),
                "ResourceNotFoundException", leak: $"could not remove target {target.Id} from rule {name} on {bus}");
            await Tolerate(
                () => ev.DeleteRuleAsync(new DeleteRuleRequest { Name = name, EventBusName = bus, Force = true }),
                "ResourceNotFoundException", leak: $"could not delete rule {name} on bus {bus}");
        }

        ctx.OnCleanup(Teardown);
        await events.PutTargetsAsync(new PutTargetsRequest { Rule = name, EventBusName = bus, Targets = [target] });
        return new RuleRef(name, arn);
    }

    /// <summary>Parse an SQS message body as JSON, falling back to the raw string.</summary>
    private static JsonNode? Body(SqsMessage msg)
    {
        var raw = msg.Body ?? string.Empty;
        try
        {
            return JsonNode.Parse(raw);
        }
        catch (JsonException)
        {
            return JsonValue.Create(raw);
        }
    }

    /// <summary>
    /// Long-poll a queue for <paramref name="windowS"/> and return every parsed body seen, deleting as it goes.
    /// </summary>
    /// <remarks>
    /// This is the NEGATIVE observable. An empty return only means something because the callers above
    /// have already proved both hops live on this exact wiring — used alone it would pass against a
    /// target that delivers nothing at all.
    /// </remarks>
    private static async Task<List<JsonNode?>> Drain(FidelityContext ctx, string queueUrl, double windowS, int maxTotal = 25)
    {
        var sqs = ctx.Sqs;
        var seen = new List<JsonNode?>();
        var started = FidelityTime.StartNew();
        var window = TimeSpan.FromSeconds(windowS);
        while (started.Elapsed < window && seen.Count < maxTotal)
        {
            var wait = Math.Min(5, Math.Max(1, (int)(window - started.Elapsed).TotalSeconds));
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = wait });
            foreach (var msg in resp.Messages ?? [])
            {
                seen.Add(Body(msg));
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
            }
        }

        return seen;
    }

    /// <summary>Poll until a message whose detail.marker == marker arrives. RAISES on timeout.</summary>
    /// <returns>(parsed body, seconds waited).</returns>
    private static async Task<(JsonObject Body, double Seconds)> AwaitMarker(FidelityContext ctx, string queueUrl, string marker, double timeoutS)
    {
        var started = FidelityTime.StartNew();
        var others = new List<JsonNode?>();
        var deadline = TimeSpan.FromSeconds(timeoutS);
        while (started.Elapsed < deadline)
        {
            foreach (var body in await Drain(ctx, queueUrl, windowS: Math.Min(8.0, Math.Max(1.0, (deadline - started.Elapsed).TotalSeconds))))
            {
                if (body is JsonObject o && (string?)o["detail"]?["marker"] == marker)
                {
                    return (o, started.Elapsed.TotalSeconds);
                }

                others.Add(body);
            }
        }

        var text = "[" + string.Join(", ", others.Select(b => b?.ToJsonString())) + "]";
        throw Fail(
            $"no message carrying detail.marker='{marker}' arrived on {queueUrl} within {timeoutS:F1}s " +
            $"(other bodies seen meanwhile: {text[..Math.Min(200, text.Length)]})");
    }

    /// <summary>
    /// PutEvents on <paramref name="bus"/> with a FRESH marker until one of them reaches <paramref name="queueUrl"/>. RAISES on timeout.
    /// </summary>
    /// <remarks>
    /// Re-emitting is required, not optional: a freshly created rule silently drops events until the
    /// routing goes live, and EventBridge never retries a dropped event. A fresh marker per attempt
    /// means the returned latency is the latency of the attempt that actually landed, not of the first
    /// one. Returns (marker, parsed body, seconds from that emission to arrival).
    /// </remarks>
    private static async Task<(string Marker, JsonObject Body, double Latency)> EmitUntilDelivered(
        FidelityContext ctx, string bus, string source, string detailType, string queueUrl, double timeoutS)
    {
        var events = ctx.Events;
        var started = FidelityTime.StartNew();
        var attempts = 0;
        while (started.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            attempts++;
            var marker = Guid.NewGuid().ToString("N");
            var sentAt = FidelityTime.StartNew();
            var resp = await events.PutEventsAsync(new PutEventsRequest
            {
                Entries =
                [
                    new PutEventsRequestEntry
                    {
                        Source = source,
                        DetailType = detailType,
                        Detail = $$$"""{"marker": "{{{marker}}}", "nested": {"n": 7, "flag": true}}""",
                        Resources = [],
                        EventBusName = bus,
                    },
                ],
            });
            Ensure(
                resp.FailedEntryCount == 0,
                $"PutEvents on bus {bus} must be accepted while establishing liveness, got {Repr(resp)}");
            foreach (var body in await Drain(ctx, queueUrl, windowS: 8.0))
            {
                if (body is JsonObject o && (string?)o["detail"]?["marker"] == marker)
                {
                    return (marker, o, Math.Max(sentAt.Elapsed.TotalSeconds, 0.001));
                }
            }
        }

        throw Fail(
            $"the {detailType} control event never reached {queueUrl} from bus {bus} after {attempts} " +
            $"PutEvents attempts over {timeoutS:F1}s — the positive leg could not be established, so the " +
            "absence assertion this control exists to license is unprovable");
    }
}

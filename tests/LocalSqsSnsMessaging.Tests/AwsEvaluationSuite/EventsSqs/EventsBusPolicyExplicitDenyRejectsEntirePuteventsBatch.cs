// Translated from behavioral/events__sqs/check_events_bus_policy_explicit_deny_rejects_entire_putevents_batch.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.EventBridge.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using SqsMessage = Amazon.SQS.Model.Message;
using RemovePermissionRequest = Amazon.EventBridge.Model.RemovePermissionRequest;
using Tag = Amazon.EventBridge.Model.Tag;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.EventsSqs;

/// <summary>events__sqs.events-bus-policy-explicit-deny-rejects-entire-putevents-batch</summary>
[Category("AwsEvaluationSuite")]
public sealed class EventsBusPolicyExplicitDenyRejectsEntirePuteventsBatch
{
    private const string XsvcPrefix = "sim2real-xsvc-";
    private const string ManagedTagKey = "sim2real:managed";

    /// <summary>
    /// An explicit Deny scoped by an events:source condition rejects the WHOLE mixed PutEvents batch
    /// with AccessDeniedException and delivers neither entry; removing the Deny restores both.
    ///
    /// Reference: https://docs.aws.amazon.com/eventbridge/latest/userguide/eb-event-bus-perms.html
    /// </summary>
    [Test]
    [DisplayName("events__sqs.events-bus-policy-explicit-deny-rejects-entire-putevents-batch")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EventBridgePermissions, "PutPermission is not implemented: it fails with NotSupportedException, so a bus policy Deny is never attached or enforced")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var deniedSource = XName("src-denied").Replace('-', '.');
        var allowedSource = XName("src-allowed").Replace('-', '.');

        var bus = XName("bus");
        var busArn = (await events.CreateEventBusAsync(new CreateEventBusRequest { Name = bus, Tags = XsvcTagsKv() })).EventBusArn;
        ctx.OnCleanup(() => ctx.Events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));

        var queue = await CreateQueue(ctx);
        await PutRuleWithSqsTarget(ctx, new JsonObject { ["source"] = new JsonArray(deniedSource, allowedSource) }, queue, bus: bus);

        // Prove the path live BEFORE breaking anything, so a later absence is suppression and not
        // warm-up: a fresh rule drops events until it goes live and EventBridge never retries a drop.
        await SettleDeliveryPath(ctx, bus, allowedSource, queue.Url, consecutive: 2);
        await Collect(ctx, queue.Url, windowS: 2.0, maxTotal: 100);

        await events.PutPermissionAsync(new PutPermissionRequest
        {
            EventBusName = bus,
            Policy = new JsonObject
            {
                ["Version"] = "2012-10-17",
                ["Statement"] = new JsonArray(new JsonObject
                {
                    ["Sid"] = "Sim2realDenyOneSource",
                    ["Effect"] = "Deny",
                    ["Principal"] = "*",
                    ["Action"] = "events:PutEvents",
                    ["Resource"] = busArn,
                    ["Condition"] = new JsonObject { ["StringEquals"] = new JsonObject { ["events:source"] = deniedSource } },
                }),
            }.ToJsonString(),
        });
        ctx.OnCleanup(() => Quiet(() => ctx.Events.RemovePermissionAsync(new RemovePermissionRequest { EventBusName = bus, RemoveAllPermissions = true })));
        var policy = (await events.DescribeEventBusAsync(new DescribeEventBusRequest { Name = bus })).Policy ?? "";
        Ensure(
            policy.Contains("Sim2realDenyOneSource", StringComparison.Ordinal),
            $"PutPermission did not attach the Deny to bus {bus}: DescribeEventBus returned Policy=" +
            $"'{policy}'");

        // Enforcement is eventually consistent (about 30 s on a warm bus), so poll to steady state.
        var (steadyCode, steadyMessage) = await PollUntilDenied(events, bus, deniedSource, timeoutS: 180.0);
        Ensure(
            steadyCode == "AccessDeniedException",
            $"a single denied-source PutEvents settled on error code '{steadyCode}', expected " +
            "'AccessDeniedException' -- an explicit Deny in a bus resource policy is a request-level " +
            "refusal, not a per-entry validation failure");
        var callerArn = ctx.CallerArn;
        var refusal =
            $"User: {callerArn} is not authorized to perform: events:PutEvents on resource: " +
            $"{busArn} with an explicit deny in a resource-based policy";
        Ensure(steadyMessage == refusal, $"unexpected refusal message: '{steadyMessage}'");
        await Collect(ctx, queue.Url, windowS: 2.0, maxTotal: 100);

        var deniedNonce = XName("n-denied");
        var allowedNonce = XName("n-allowed");
        List<PutEventsRequestEntry> mixed =
        [
            Entry(deniedSource, "MixedBatch", deniedNonce, bus),
            Entry(allowedSource, "MixedBatch", allowedNonce, bus),
        ];
        var (code, message) = await Error(() => events.PutEventsAsync(new PutEventsRequest { Entries = [.. mixed] }));
        Ensure(
            code == "AccessDeniedException",
            "a two-entry PutEvents carrying one denied source and one allowed source settled on " +
            $"'{code}', expected 'AccessDeniedException' for the whole request -- treating the " +
            "condition as a per-entry filter is the over-permissive reading real AWS does not implement");
        Ensure(message == refusal, $"unexpected mixed-batch refusal message: '{message}'");

        // Positive control, same file: the same allowed SOURCE on its own is accepted and really arrives.
        // It carries its own nonce, deliberately -- reusing the mixed batch's nonce would make the two
        // indistinguishable in the queue and the absence assertion below unfalsifiable.
        var controlNonce = XName("n-control");
        var controlSent = FidelityTime.StartNew();
        var control = await events.PutEventsAsync(new PutEventsRequest
        {
            Entries = [Entry(allowedSource, "MixedBatch", controlNonce, bus)],
        });
        Ensure(
            control.FailedEntryCount == 0,
            $"the positive control (the allowed entry on its own) was not accepted: {Repr(control)} -- the " +
            "Deny is broader than the one source it names, so nothing below is attributable");
        Ensure(!string.IsNullOrEmpty(control.Entries?[0].EventId), $"the positive control returned no EventId: {Repr(control)}");
        var (got, others) = await WaitFor(ctx, queue.Url, b => NonceOf(b) == controlNonce, timeoutS: 120.0);
        var controlLatencyS = controlSent.Elapsed.TotalSeconds;
        Ensure(
            got.Count > 0,
            $"the positive control's event '{controlNonce}' never reached {queue.Name}, so the " +
            "absences below cannot be distinguished from a dead delivery path (other messages seen: " +
            $"[{string.Join(", ", others.Select(m => Body(m)?.ToJsonString()))}])");

        // The control has arrived, so the mixed batch -- issued earlier -- has had strictly longer to
        // arrive. Anything from it within a further 3x the control's latency would be delivery, not lag.
        var absenceS = Math.Max(5.0, 3.0 * controlLatencyS);
        var leaked = others.Select(Body).Concat((await Collect(ctx, queue.Url, windowS: absenceS, maxTotal: 100)).Select(Body)).ToList();
        var leakedNonces = leaked
            .Select(NonceOf)
            .Where(n => n == deniedNonce || n == allowedNonce)
            .Order(StringComparer.Ordinal)
            .ToList();
        Ensure(
            leakedNonces.Count == 0,
            $"the refused batch still delivered [{string.Join(", ", leakedNonces.Select(n => $"'{n}'"))}] to {queue.Name} after the positive " +
            "control had already arrived. A request that raised AccessDeniedException must publish " +
            "NEITHER entry -- not even the entry whose source the Deny does not name");

        var still = (await events.DescribeEventBusAsync(new DescribeEventBusRequest { Name = bus })).Policy ?? "";
        Ensure(
            still.Contains("Sim2realDenyOneSource", StringComparison.Ordinal),
            $"the Deny statement vanished from bus {bus} during the check, so the refusal above was not " +
            $"measured against a stable policy: DescribeEventBus now returns Policy='{still}'");

        // Remove only the Deny statement; the same two-entry shape must now succeed with BOTH entries.
        await events.RemovePermissionAsync(new RemovePermissionRequest { EventBusName = bus, StatementId = "Sim2realDenyOneSource" });
        var restoredNonceDenied = XName("n-restored-denied");
        var restoredNonceAllowed = XName("n-restored-allowed");
        List<PutEventsRequestEntry> restoredBatch =
        [
            Entry(deniedSource, "MixedBatch", restoredNonceDenied, bus),
            Entry(allowedSource, "MixedBatch", restoredNonceAllowed, bus),
        ];
        var restored = await PutUntilAccepted(events, restoredBatch, timeoutS: 180.0);
        Ensure(
            restored.FailedEntryCount == 0,
            "after removing the Deny statement, the identical two-entry batch shape (formerly-denied " +
            $"source + allowed source) still did not fully succeed: {Repr(restored)} -- removing a " +
            "request-level Deny must restore the whole request, not just the entry it never named");

        var restoredLeakedNonces = new HashSet<string>(StringComparer.Ordinal) { restoredNonceDenied, restoredNonceAllowed };
        var seenRestored = new HashSet<string>(StringComparer.Ordinal);

        bool RestoredSeen(JsonNode? body)
        {
            if (NonceOf(body) is { } nonce && restoredLeakedNonces.Contains(nonce))
            {
                seenRestored.Add(nonce);
            }

            return seenRestored.SetEquals(restoredLeakedNonces);
        }

        await WaitFor(ctx, queue.Url, RestoredSeen, timeoutS: 60.0);
        Ensure(
            seenRestored.SetEquals(restoredLeakedNonces),
            $"after removing the Deny, only {SortedRepr(seenRestored)} of the restored batch's nonces " +
            $"{SortedRepr(restoredLeakedNonces)} reached '{queue.Name}' -- a request that is fully " +
            "accepted (FailedEntryCount == 0) must deliver every entry, not a subset");
    }

    private sealed record Queue(string Name, string Url, string Arn);

    private static PutEventsRequestEntry Entry(string source, string detailType, string nonce, string bus) => new()
    {
        Source = source,
        DetailType = detailType,
        Detail = $$"""{"nonce": "{{nonce}}"}""",
        EventBusName = bus,
    };

    private static string SortedRepr(IEnumerable<string> values) =>
        $"[{string.Join(", ", values.Order(StringComparer.Ordinal).Select(v => $"'{v}'"))}]";

    private static string Repr(PutEventsResponse? r) =>
        r is null
            ? "None"
            : $"{{'FailedEntryCount': {r.FailedEntryCount}, 'Entries': [{string.Join(", ", (r.Entries ?? []).Select(e => $"{{'EventId': '{e.EventId}', 'ErrorCode': '{e.ErrorCode}', 'ErrorMessage': '{e.ErrorMessage}'}}"))}]}}";

    private static string? NonceOf(JsonNode? body) =>
        body is JsonObject o && o["detail"] is JsonObject d && d["nonce"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static Dictionary<string, string> XsvcResourceTags() =>
        new() { [ManagedTagKey] = "true", ["sim2real:prefix"] = XsvcPrefix.TrimEnd('-') };

    private static List<Tag> XsvcTagsKv() =>
        [.. XsvcResourceTags().Select(kv => new Tag { Key = kv.Key, Value = kv.Value })];

    /// <summary>A unique, prefix-tagged resource name: sim2real-xsvc-&lt;kind&gt;-&lt;uuid&gt;.</summary>
    private static string XName(string kind) => FidelityContext.UniqueName($"{XsvcPrefix}{kind}-");

    /// <summary>Teardown helper: a delete that races another delete must not mask a verdict.</summary>
    private static async Task Quiet(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is not null && error.Code is not ("AWS.SimpleQueueService.NonExistentQueue" or "QueueDoesNotExist" or "ResourceNotFoundException"))
        {
            throw error.Exception;
        }
    }

    /// <summary>
    /// Return (error code, message) for a call that fails, and RAISE if it unexpectedly succeeds:
    /// an emulator accepting input real AWS refuses must not read like a code mismatch.
    /// </summary>
    private static async Task<(string Code, string Message)> Error(Func<Task<PutEventsResponse>> fn)
    {
        var (resp, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code ?? "", error.Message ?? "");
        }

        throw Fail($"expected the call to be refused but it SUCCEEDED and returned {Repr(resp)}");
    }

    /// <summary>
    /// PutEvents one entry on `source` until the call is refused; return its (code, message).
    /// </summary>
    /// <remarks>A bus resource policy is eventually consistent: a Deny on a warm bus can take ~30 s to engage.</remarks>
    private static async Task<(string Code, string Message)> PollUntilDenied(Amazon.EventBridge.IAmazonEventBridge events, string bus, string source, double timeoutS)
    {
        var started = FidelityTime.StartNew();
        PutEventsResponse? last = null;
        while (started.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var (resp, error) = await FidelityContext.Capture(() => events.PutEventsAsync(new PutEventsRequest
            {
                Entries = [Entry(source, "DenyProbe", "steady-state-probe", bus)],
            }));
            if (error is not null)
            {
                return (error.Code ?? "", error.Message ?? "");
            }

            last = resp;
            await FidelityTime.Delay(TimeSpan.FromSeconds(2.0));
        }

        throw Fail(
            $"the explicit Deny on bus {bus} for source '{source}' never took effect within " +
            $"{timeoutS}s -- PutEvents was still accepted, last response {Repr(last)}. The policy is " +
            "stored (DescribeEventBus returns it) but not enforced, which is the over-permissive " +
            "failure this check exists to catch");
    }

    /// <summary>
    /// PutEvents `entries` until fully accepted (FailedEntryCount == 0); return that response.
    /// Removing a Deny is eventually consistent like adding one; raises on timeout.
    /// </summary>
    private static async Task<PutEventsResponse> PutUntilAccepted(Amazon.EventBridge.IAmazonEventBridge events, List<PutEventsRequestEntry> entries, double timeoutS)
    {
        var started = FidelityTime.StartNew();
        PutEventsResponse? last = null;
        string? lastCode = null;
        while (started.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var (resp, error) = await FidelityContext.Capture(() => events.PutEventsAsync(new PutEventsRequest { Entries = [.. entries] }));
            if (error is not null)
            {
                lastCode = error.Code;
                last = null;
            }
            else
            {
                last = resp;
                lastCode = null;
                if (last!.FailedEntryCount == 0)
                {
                    return last;
                }
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2.0));
        }

        throw Fail(
            $"the batch was never fully accepted within {timeoutS}s after removing the Deny -- " +
            $"last error code={(lastCode is null ? "None" : $"'{lastCode}'")} last response={Repr(last)}");
    }

    /// <summary>Create an SQS queue (registers delete_queue teardown). Returns {name,url,arn}.</summary>
    private static async Task<Queue> CreateQueue(FidelityContext ctx, bool fifo = false, Dictionary<string, string>? attrs = null)
    {
        var baseName = XName("q");
        var name = fifo ? $"{baseName}.fifo" : baseName;
        var sqs = ctx.Sqs;
        var createAttrs = new Dictionary<string, string>(attrs ?? []);
        if (fifo)
        {
            createAttrs["FifoQueue"] = "true";
        }

        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Attributes = createAttrs, Tags = XsvcResourceTags() })).QueueUrl;
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes["QueueArn"];
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        return new Queue(name, url, arn);
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
    /// Drain a queue over a bounded window with long-polling ReceiveMessage(MaxNumberOfMessages=10)
    /// and return EVERY message seen. Never raises and never swallows an assertion.
    /// </summary>
    /// <remarks>An empty return is only meaningful once a positive control has already been observed.</remarks>
    private static async Task<List<SqsMessage>> Collect(FidelityContext ctx, string queueUrl, double windowS = 30.0, int maxTotal = 10, bool keep = false)
    {
        var sqs = ctx.Sqs;
        var seen = new List<SqsMessage>();
        var started = FidelityTime.StartNew();
        var window = TimeSpan.FromSeconds(windowS);
        while (started.Elapsed < window && seen.Count < maxTotal)
        {
            var wait = Math.Min(5, Math.Max(1, (int)(window - started.Elapsed).TotalSeconds));
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = wait,
                MessageAttributeNames = ["All"],
            });
            foreach (var msg in resp.Messages ?? [])
            {
                seen.Add(msg);
                if (!keep)
                {
                    await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                }
            }
        }

        return seen;
    }

    /// <summary>
    /// Poll a queue until a message whose parsed body satisfies predicate() arrives, returning at once.
    /// Returns (matched, others) -- every non-matching message seen along the way is returned too so
    /// the caller can assert on leakage instead of silently discarding it.
    /// </summary>
    private static async Task<(List<SqsMessage> Matched, List<SqsMessage> Others)> WaitFor(
        FidelityContext ctx, string queueUrl, Func<JsonNode?, bool> predicate, double timeoutS = 60.0)
    {
        var sqs = ctx.Sqs;
        var matched = new List<SqsMessage>();
        var others = new List<SqsMessage>();
        var started = FidelityTime.StartNew();
        var deadline = TimeSpan.FromSeconds(timeoutS);
        while (started.Elapsed < deadline && matched.Count == 0)
        {
            var wait = Math.Min(5, Math.Max(1, (int)(deadline - started.Elapsed).TotalSeconds));
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = wait,
                MessageAttributeNames = ["All"],
            });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                (predicate(Body(msg)) ? matched : others).Add(msg);
            }
        }

        return (matched, others);
    }

    /// <summary>
    /// Emit single control events until `consecutive` of them round-trip back to back.
    /// </summary>
    /// <remarks>
    /// A rule and its target become effective progressively and a dropped event is never retried,
    /// so one arrival proves only that SOME path is live.
    /// </remarks>
    private static async Task SettleDeliveryPath(FidelityContext ctx, string bus, string source, string queueUrl, int consecutive = 2)
    {
        var events = ctx.Events;
        var streak = 0;
        for (var i = 0; i < 30; i++)
        {
            var nonce = XName("warm");
            await events.PutEventsAsync(new PutEventsRequest { Entries = [Entry(source, "Warmup", nonce, bus)] });
            var (got, _) = await WaitFor(ctx, queueUrl, b => b is JsonObject o && (string?)o["detail"]?["nonce"] == nonce, timeoutS: 15.0);
            streak = got.Count > 0 ? streak + 1 : 0;
            if (streak >= consecutive)
            {
                return;
            }
        }

        throw Fail(
            $"the rule -> {queueUrl} delivery path never round-tripped {consecutive} consecutive " +
            "control events before the Deny was applied, so no absence measured afterwards would be " +
            $"attributable to the Deny (longest streak reached: {streak})");
    }

    /// <summary>A queue policy granting one AWS service SendMessage on this queue, scoped by SourceArn.</summary>
    private static JsonObject AllowServiceSend(string queueArn, string service, string sourceArn) => new()
    {
        ["Version"] = "2012-10-17",
        ["Statement"] = new JsonArray(new JsonObject
        {
            ["Sid"] = "AllowServiceSend",
            ["Effect"] = "Allow",
            ["Principal"] = new JsonObject { ["Service"] = service },
            ["Action"] = "sqs:SendMessage",
            ["Resource"] = queueArn,
            ["Condition"] = new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = sourceArn } },
        }),
    };

    private static Task SetQueuePolicy(FidelityContext ctx, string queueUrl, JsonObject policy) =>
        ctx.Sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            Attributes = new() { ["Policy"] = policy.ToJsonString() },
        });

    /// <summary>
    /// Create an EventBridge rule for `pattern` on `bus`, grant the rule SendMessage on `queue`, and
    /// attach the queue as target t1. Registers remove_targets + delete_rule teardown (reverse order of
    /// creation). Returns the rule name.
    /// </summary>
    private static async Task<string> PutRuleWithSqsTarget(FidelityContext ctx, JsonObject pattern, Queue queue, string bus)
    {
        var events = ctx.Events;
        var name = XName("rule");
        var ruleArn = (await events.PutRuleAsync(new PutRuleRequest
        {
            Name = name,
            EventBusName = bus,
            EventPattern = pattern.ToJsonString(),
        })).RuleArn;
        ctx.OnCleanup(() => ctx.Events.DeleteRuleAsync(new DeleteRuleRequest { Name = name, EventBusName = bus, Force = true }));
        await SetQueuePolicy(ctx, queue.Url, AllowServiceSend(queue.Arn, "events.amazonaws.com", ruleArn));
        await events.PutTargetsAsync(new PutTargetsRequest { Rule = name, EventBusName = bus, Targets = [new Target { Id = "t1", Arn = queue.Arn }] });
        ctx.OnCleanup(() => ctx.Events.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = name, EventBusName = bus, Ids = ["t1"], Force = true }));
        return name;
    }
}

// Translated from behavioral/events__sqs/check_replay_redelivers_each_archived_event_exactly_once_and_never_the_out_of_window_one.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.EventsSqs;

/// <summary>events__sqs.replay-redelivers-each-archived-event-exactly-once-and-never-the-out-of-window-one</summary>
[Category("AwsEvaluationSuite")]
public sealed class ReplayRedeliversEachArchivedEventExactlyOnceAndNeverTheOutOfWindowOne
{
    private const string Check = "replay-redelivers-each-archived-event-exactly-once-and-never-the-out-of-window-one";
    private const string Source = "sim2real.nd.replayonce";
    private const string DetailType = "ReplayCardinality";

    // ===== helpers =====

    /// <summary>A bounded retry that RAISES on its deadline; never returns its last value on timeout.</summary>
    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, Func<T, string> repr, double timeoutS, double intervalS = 3.0, string description = "condition")
    {
        var started = FidelityTime.StartNew();
        T? last = default;
        while (started.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"{description} not met within {timeoutS.ToString(CultureInfo.InvariantCulture)}s. Last result: {(last is null ? "None" : repr(last))}");
    }

    private static async Task<(string Url, string Arn)> MakeQueue(FidelityContext ctx, string name, string sourceArnLike, string checkTag)
    {
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Tags = new() { ["sim2real-check"] = checkTag } })).QueueUrl;
        ctx.OnCleanup(() => ctx.Sqs.DeleteQueueAsync(url));
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes["QueueArn"];
        var policy = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Sid"] = "AllowEventBridge",
                ["Effect"] = "Allow",
                ["Principal"] = new JsonObject { ["Service"] = "events.amazonaws.com" },
                ["Action"] = "sqs:SendMessage",
                ["Resource"] = arn,
                ["Condition"] = new JsonObject { ["ArnLike"] = new JsonObject { ["aws:SourceArn"] = sourceArnLike } },
            }),
        };
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = url, Attributes = new() { ["Policy"] = policy.ToJsonString() } });
        return (url, arn);
    }

    /// <summary>
    /// Long-poll for `seconds`, deleting what it sees. `.get("Messages", [])` feeds a loop whose
    /// result is asserted by exact count, which is the sanctioned polarity for the empty default.
    /// </summary>
    private static async Task<List<JsonObject>> Drain(FidelityContext ctx, string url, double seconds)
    {
        var sqs = ctx.Sqs;
        var @out = new List<JsonObject>();
        var started = FidelityTime.StartNew();
        while (started.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
            foreach (var msg in resp.Messages ?? [])
            {
                @out.Add(JsonNode.Parse(msg.Body)!.AsObject());
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle });
            }
        }

        return @out;
    }

    private static async Task<int> QueueDepth(FidelityContext ctx, string url)
    {
        var attrs = (await ctx.Sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = url,
            AttributeNames = ["ApproximateNumberOfMessages", "ApproximateNumberOfMessagesNotVisible", "ApproximateNumberOfMessagesDelayed"],
        })).Attributes ?? [];
        return attrs.Values.Sum(v => int.Parse(v, CultureInfo.InvariantCulture));
    }

    private static async Task PutTargetsOk(AmazonEventBridgeClient events, string bus, string rule, List<Target> targets)
    {
        var resp = await events.PutTargetsAsync(new PutTargetsRequest { Rule = rule, EventBusName = bus, Targets = targets });
        Ensure(
            resp.FailedEntryCount == 0,
            $"put_targets on {rule} reported failures, so the wiring under test does not exist: " +
            $"[{string.Join(", ", (resp.FailedEntries ?? []).Select(f => $"{{'TargetId': '{f.TargetId}', 'ErrorCode': '{f.ErrorCode}', 'ErrorMessage': '{f.ErrorMessage}'}}"))}]");
    }

    private static async Task DeleteArchive(FidelityContext ctx, string name)
    {
        var error = await FidelityContext.CaptureError(() => ctx.Events.DeleteArchiveAsync(new DeleteArchiveRequest { ArchiveName = name }));
        if (error is not null && error.Code != "ResourceNotFoundException")
        {
            throw error.Exception;
        }
    }

    // ===== the check =====

    /// <summary>
    /// A replay redelivers each archived event exactly once — not zero, not twice — and never
    /// redelivers the event its window excluded.
    /// </summary>
    /// <remarks>
    /// Reference: https://docs.aws.amazon.com/eventbridge/latest/APIReference/API_StartReplay.html
    /// Reference: https://docs.aws.amazon.com/eventbridge/latest/userguide/eb-archive-event.html
    /// </remarks>
    [Test]
    [DisplayName("events__sqs.replay-redelivers-each-archived-event-exactly-once-and-never-the-out-of-window-one")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EventBridgeArchives, "CreateArchive is not implemented: it fails with NotSupportedException, so no archive or replay exists")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var account = ctx.Account;
        const string checkTag = Check;
        var suffix = Guid.NewGuid().ToString("N")[..10];

        var bus = FidelityContext.UniqueName("sim2real-nd-replay-bus-");
        var busArn = (await events.CreateEventBusAsync(new CreateEventBusRequest
        {
            Name = bus,
            Tags = [new Amazon.EventBridge.Model.Tag { Key = "sim2real-check", Value = checkTag }],
        })).EventBusArn;
        ctx.OnCleanup(() => ctx.Events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));

        var (queueUrl, queueArn) = await MakeQueue(
            ctx,
            $"sim2real-nd-replay-{suffix}",
            sourceArnLike: $"arn:*:events:{ctx.Region}:{account}:rule/*",
            checkTag: checkTag);

        var rule = FidelityContext.UniqueName("sim2real-nd-replay-rule-");
        var ruleArn = (await events.PutRuleAsync(new PutRuleRequest
        {
            Name = rule,
            EventBusName = bus,
            EventPattern = $$"""{"source": ["{{Source}}"], "detail-type": ["{{DetailType}}"]}""",
            State = RuleState.ENABLED,
            Tags = [new Amazon.EventBridge.Model.Tag { Key = "sim2real-check", Value = checkTag }],
        })).RuleArn;
        ctx.OnCleanup(() => TeardownRule(ctx, bus, rule));
        await PutTargetsOk(events, bus, rule, [new Target { Id = "q", Arn = queueArn }]);

        var archive = FidelityContext.UniqueName("sim2real-nd-replay-arch-");
        await events.CreateArchiveAsync(new CreateArchiveRequest
        {
            ArchiveName = archive,
            EventSourceArn = busArn,
            EventPattern = $$"""{"source": ["{{Source}}"]}""",
            RetentionDays = 1,
        });
        ctx.OnCleanup(() => DeleteArchive(ctx, archive));
        var enabled = await PollUntil(
            () => events.DescribeArchiveAsync(new DescribeArchiveRequest { ArchiveName = archive }),
            a => a.State?.Value is "ENABLED" or "CREATE_FAILED",
            ArchiveRepr,
            timeoutS: 180,
            intervalS: 5,
            description: $"archive {archive} settled out of CREATING");
        Ensure(
            enabled.State?.Value == "ENABLED",
            $"archive {archive} settled at State='{enabled.State?.Value}' " +
            $"({Repr(enabled.StateReason)}), expected 'ENABLED' — nothing can be replayed from an " +
            "archive that never came up");

        string[] inside = [$"inside-a-{suffix}", $"inside-b-{suffix}"];
        var outside = $"outside-{suffix}";

        // Phase 1: the two events that will fall INSIDE the replay window.
        var t0 = FidelityTime.StartNew();
        var boundaryOpen = FidelityTime.UtcNow.UtcDateTime - TimeSpan.FromSeconds(30);
        var resp = await events.PutEventsAsync(new PutEventsRequest
        {
            Entries = [.. inside.Select(n => new PutEventsRequestEntry { EventBusName = bus, Source = Source, DetailType = DetailType, Detail = $$"""{"nonce": "{{n}}"}""" })],
        });
        Ensure(
            resp.FailedEntryCount == 0,
            "PutEvents did not accept both inside-window entries, so nothing below is attributable: " +
            PutEventsRepr(resp));

        // The live delivery is the positive control on this exact rule->queue wiring and it MEASURES L.
        // Accumulate ACROSS polling windows, not just within the latest one: `_drain` deletes every
        // message it sees, so if the two entries from one PutEvents call happen to land in separate
        // 6s windows, checking only the latest window's batch loses the earlier arrival forever and
        // spuriously times out a target that delivered correctly, just not within a single window.
        var liveByNonce = new Dictionary<string, JsonObject>();
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(180.0) && liveByNonce.Keys.ToHashSet().IsProperSubsetOf(inside))
        {
            foreach (var msg in await Drain(ctx, queueUrl, seconds: 6))
            {
                liveByNonce[Nonce(msg)] = msg;
            }
        }

        var latency = Math.Max(t0.Elapsed.TotalSeconds, 1.0);
        var live = liveByNonce.Values.ToList();
        Ensure(
            liveByNonce.Keys.ToHashSet().SetEquals(inside),
            $"the live delivery reached {Repr(liveByNonce.Keys.Order(StringComparer.Ordinal))} within 180s, expected exactly " +
            $"{Repr(inside.Order(StringComparer.Ordinal))} — a contaminated or incomplete queue makes every count below " +
            "meaningless");
        Ensure(
            !live.Any(m => m.ContainsKey("replay-name")),
            "a LIVE delivery already carries a replay-name field, which would make the replay " +
            "attribution below unfalsifiable: " +
            $"[{string.Join(", ", live.Where(m => m.ContainsKey("replay-name")).Take(1).Select(PyRepr))}]");

        // Phase 2: the event that will fall OUTSIDE the window. The exclusion is STRUCTURAL — the
        // replay's EventEndTime is fixed before this event exists, so no wait decides it.
        var boundaryClose = FidelityTime.UtcNow.UtcDateTime;
        // Not a barrier and not a sleep: a bounded wait on the LIVE clock so the next event's
        // archived `time` is unambiguously after EventEndTime even at second granularity. It raises if
        // the clock does not advance, which is itself a finding.
        await PollUntil(
            () => Task.FromResult(FidelityTime.UtcNow.UtcDateTime),
            now => now >= boundaryClose + TimeSpan.FromSeconds(2),
            IsoFormat,
            timeoutS: 30,
            intervalS: 0.5,
            description:
                $"the wall clock advanced past EventEndTime {IsoFormat(boundaryClose)} by 2s before " +
                "the out-of-window event was published");
        var respOut = await events.PutEventsAsync(new PutEventsRequest
        {
            Entries = [new PutEventsRequestEntry { EventBusName = bus, Source = Source, DetailType = DetailType, Detail = $$"""{"nonce": "{{outside}}"}""" }],
        });
        Ensure(respOut.FailedEntryCount == 0, $"PutEvents did not accept the outside-window entry: {PutEventsRepr(respOut)}");
        var liveOut = await PollUntil(
            () => Drain(ctx, queueUrl, seconds: 6),
            msgs => msgs.Any(m => Nonce(m) == outside),
            msgs => $"[{string.Join(", ", msgs.Select(PyRepr))}]",
            timeoutS: 180,
            intervalS: 0,
            description:
                $"the outside-window event '{outside}' was delivered LIVE — proof it EXISTS and was " +
                "archivable, so its later absence is an exclusion and not a lost event");
        Ensure(
            liveOut.Any(m => Nonce(m) == outside),
            $"the outside-window event never arrived live: [{string.Join(", ", liveOut.Select(PyRepr))}]");

        var ingested = await PollUntil(
            () => events.DescribeArchiveAsync(new DescribeArchiveRequest { ArchiveName = archive }),
            a => (a.EventCount ?? 0) >= 3,
            ArchiveRepr,
            timeoutS: 1500,
            intervalS: 10,
            description:
                $"archive {archive} counted all three published events (measured on real AWS: the " +
                "sibling check on this same archive->replay path saw EventCount lag PutEvents by " +
                "151s/353s/559s/900s+ across four runs, so this poll is generous and FAILS on " +
                "timeout rather than truncating early)");
        Ensure(
            (ingested.SizeBytes ?? 0) > 0,
            $"archive {archive} reports EventCount={Num(ingested.EventCount)} but " +
            $"SizeBytes={Num(ingested.SizeBytes)} — nothing was stored, so a replay could not " +
            "re-emit anything and the cardinality claims below would be vacuous");

        // Phase 3: replay only the window that contains the two inside events.
        var replay = FidelityContext.UniqueName("sim2real-nd-replay-");
        await events.StartReplayAsync(new StartReplayRequest
        {
            ReplayName = replay,
            EventSourceArn = ingested.ArchiveArn,
            EventStartTime = boundaryOpen,
            EventEndTime = boundaryClose,
            Destination = new ReplayDestination { Arn = busArn, FilterArns = [ruleArn] },
        });
        var settled = await PollUntil(
            () => events.DescribeReplayAsync(new DescribeReplayRequest { ReplayName = replay }),
            r => r.State?.Value is "COMPLETED" or "FAILED" or "CANCELLED",
            r => $"{{'ReplayName': '{r.ReplayName}', 'State': '{r.State?.Value}', 'StateReason': '{r.StateReason}'}}",
            timeoutS: 300,
            intervalS: 5,
            description: $"replay {replay} reached a terminal state");
        Ensure(
            settled.State?.Value == "COMPLETED",
            $"replay {replay} settled at State='{settled.State?.Value}' " +
            $"({Repr(settled.StateReason)}), expected 'COMPLETED'");

        // Phase 4: the cardinality claim. Drain for 3L AFTER the replay is COMPLETED, so a correct but
        // slower target is not graded as having blocked, and a duplicate has time to show up.
        var hold = Math.Max(3 * latency, 15.0);
        var redelivered = await Drain(ctx, queueUrl, seconds: hold);
        var counts = redelivered.GroupBy(Nonce).ToDictionary(g => g.Key, g => g.Count());
        Ensure(
            counts.GetValueOrDefault(outside, 0) == 0,
            $"the replay over [{IsoFormat(boundaryOpen)}, {IsoFormat(boundaryClose)}] redelivered " +
            $"the out-of-window event '{outside}' {(counts.TryGetValue(outside, out var oc) ? oc.ToString(CultureInfo.InvariantCulture) : "None")} time(s) during a {hold.ToString("0", CultureInfo.InvariantCulture)}s " +
            "drain, expected 0 — its archived `time` is after EventEndTime");
        foreach (var nonce in inside)
        {
            Ensure(
                counts.GetValueOrDefault(nonce, 0) == 1,
                $"the replay redelivered '{nonce}' {counts.GetValueOrDefault(nonce, 0)} time(s) over a {hold.ToString("0", CultureInfo.InvariantCulture)}s " +
                $"drain (3x the {latency.ToString("0", CultureInfo.InvariantCulture)}s measured live latency), expected EXACTLY 1 — 0 means the " +
                "replay re-emitted nothing, >1 means it is at-least-once where this asserts once. All " +
                $"counts: {{{string.Join(", ", counts.Select(kv => $"'{kv.Key}': {kv.Value}"))}}}");
        }

        var known = inside.Append(outside).ToHashSet();
        var strangers = redelivered.Where(m => !known.Contains(Nonce(m))).ToList();
        Ensure(
            strangers.Count == 0,
            $"{strangers.Count} redelivered message(s) carry none of this check's nonces, so the exact " +
            $"counts above were measured against contaminated traffic: [{string.Join(", ", strangers.Take(2).Select(PyRepr))}]");
        foreach (var msg in redelivered)
        {
            var replayName = msg["replay-name"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
            Ensure(
                replayName == replay,
                $"redelivered event '{Nonce(msg)}' carries " +
                $"replay-name={Repr(replayName)}, expected '{replay}' — without that marker it " +
                "is indistinguishable from a late LIVE delivery, and 'exactly once' would be measured " +
                "over the wrong population");
        }

        // A second independent observable, plus a freeze: a COMPLETED replay is terminal.
        Ensure(
            await QueueDepth(ctx, queueUrl) == 0,
            $"the queue reports depth {await QueueDepth(ctx, queueUrl)} behind an empty receive — a second " +
            "replayed copy is in flight, so 'exactly once' is not stable");
        var tail = await Drain(ctx, queueUrl, seconds: Math.Max(latency, 15.0));
        Ensure(
            tail.Count == 0,
            $"{tail.Count} further message(s) arrived after the COMPLETED replay's drain: " +
            $"{Repr(tail.Select(Nonce))} — a terminal replay must not keep emitting");
    }

    private static async Task TeardownRule(FidelityContext ctx, string bus, string rule)
    {
        var events = ctx.Events;
        await events.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = rule, EventBusName = bus, Ids = ["q"], Force = true });
        await events.DeleteRuleAsync(new DeleteRuleRequest { Name = rule, EventBusName = bus, Force = true });
    }

    /// <summary>msg["detail"]["nonce"].</summary>
    private static string Nonce(JsonObject msg) => msg["detail"]!["nonce"]!.GetValue<string>();

    private static string Repr(string? s) => s is null ? "None" : $"'{s}'";

    private static string Repr(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(i => $"'{i}'")) + "]";

    private static string Num(long? n) => n is { } v ? v.ToString(CultureInfo.InvariantCulture) : "None";

    /// <summary>datetime.isoformat() of a UTC-aware datetime.</summary>
    private static string IsoFormat(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'+00:00'", CultureInfo.InvariantCulture);

    private static string ArchiveRepr(DescribeArchiveResponse a) =>
        $"{{'ArchiveName': '{a.ArchiveName}', 'State': '{a.State?.Value}', 'StateReason': {Repr(a.StateReason)}, 'EventCount': {Num(a.EventCount)}, 'SizeBytes': {Num(a.SizeBytes)}}}";

    private static string PutEventsRepr(PutEventsResponse resp) =>
        $"{{'FailedEntryCount': {resp.FailedEntryCount}, 'Entries': " +
        $"[{string.Join(", ", (resp.Entries ?? []).Select(e => $"{{'EventId': '{e.EventId}', 'ErrorCode': '{e.ErrorCode}', 'ErrorMessage': '{e.ErrorMessage}'}}"))}]}}";

    /// <summary>Python's repr() of a parsed JSON value, for the assertion messages.</summary>
    private static string PyRepr(JsonNode? node) => node switch
    {
        null => "None",
        JsonObject o => "{" + string.Join(", ", o.Select(kv => $"'{kv.Key}': {PyRepr(kv.Value)}")) + "}",
        JsonArray a => "[" + string.Join(", ", a.Select(PyRepr)) + "]",
        JsonValue v when v.TryGetValue<string>(out var s) => $"'{s}'",
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "True" : "False",
        _ => node.ToJsonString(),
    };
}

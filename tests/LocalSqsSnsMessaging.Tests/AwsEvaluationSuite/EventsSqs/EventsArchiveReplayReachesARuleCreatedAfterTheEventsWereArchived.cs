// Translated from behavioral/events__sqs/check_events_archive_replay_reaches_a_rule_created_after_the_events_were_archived.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.EventBridge.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using SqsMessage = Amazon.SQS.Model.Message;
using Tag = Amazon.EventBridge.Model.Tag;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.EventsSqs;

/// <summary>events__sqs.events-archive-replay-reaches-a-rule-created-after-the-events-were-archived</summary>
[Category("AwsEvaluationSuite")]
public sealed class EventsArchiveReplayReachesARuleCreatedAfterTheEventsWereArchived
{
    private const string XsvcPrefix = "sim2real-xsvc-";
    private const string ManagedTagKey = "sim2real:managed";

    /// <summary>
    /// A replay routes archived events through the rules as they exist now: a rule created after
    /// archival receives the replayed event, tagged with `replay-name` and keeping its original `time`.
    /// </summary>
    [Test]
    [DisplayName("events__sqs.events-archive-replay-reaches-a-rule-created-after-the-events-were-archived")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EventBridgeArchives, "CreateArchive is not implemented: it fails with NotSupportedException")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var source = XName("src").Replace('-', '.');

        var bus = XName("bus");
        var busArn = (await events.CreateEventBusAsync(new CreateEventBusRequest { Name = bus, Tags = XsvcTagsKv() })).EventBusArn;
        ctx.OnCleanup(() => ctx.Events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));

        var archive = XName("arc");
        var archiveArn = (await events.CreateArchiveAsync(new CreateArchiveRequest
        {
            ArchiveName = archive,
            EventSourceArn = busArn,
            EventPattern = new JsonObject { ["source"] = new JsonArray(source) }.ToJsonString(),
        })).ArchiveArn;
        ctx.OnCleanup(() => DeleteArchiveAndWait(ctx, archive));
        var enabled = await WaitArchiveEnabled(events, archive, timeoutS: BudgetS(ctx, 300.0), ctx: ctx);
        Ensure(
            enabled.EventCount == 0,
            $"archive {archive} reported EventCount={enabled.EventCount} before any event was " +
            $"published: {Repr(enabled)}");

        // An hour in the past, so current-time warm-up events are archived but never fall in the
        // replay window.
        var now = FidelityTime.UtcNow.AddHours(-1);
        var eventTime = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, TimeSpan.Zero);
        var nonce = XName("n");
        var detail = new JsonObject { ["nonce"] = nonce, ["leg"] = "archived-before-the-rule" };
        var resp = await events.PutEventsAsync(new PutEventsRequest
        {
            Entries =
            [
                new PutEventsRequestEntry
                {
                    Time = eventTime.UtcDateTime,
                    Source = source,
                    DetailType = "Replayable",
                    Detail = PyJson(detail),
                    EventBusName = bus,
                },
            ],
        });
        Ensure(
            resp.FailedEntryCount == 0 && !string.IsNullOrEmpty(resp.Entries?[0].EventId),
            $"PutEvents did not accept the entry, so nothing below is attributable to the archive: {Repr(resp)}");

        var ingested = await PollUntil(
            () => events.DescribeArchiveAsync(new DescribeArchiveRequest { ArchiveName = archive }),
            d => (d.EventCount ?? 0) >= 1,
            Repr,
            timeoutS: BudgetS(ctx, 1500.0),
            intervalS: 1.0,
            description:
                $"archive {archive} ingested the published event (real AWS measured 151 s to beyond " +
                "900 s for this lag; AWS documents no bound)",
            ctx: ctx);
        Ensure(
            (ingested.SizeBytes ?? 0) > 0,
            $"the archive counted the event but reports SizeBytes={ingested.SizeBytes}: {Repr(ingested)}");

        // No customer rule may exist yet. AWS adds a managed Events-Archive-<archive> rule for the
        // archive's own ingest, which is excluded without requiring it.
        var rulesBefore = (await ListRuleNames(events, bus)).Where(r => r != $"Events-Archive-{archive}").ToList();
        Ensure(
            rulesBefore.Count == 0,
            $"bus {bus} (created by this check) already had customer rules [{string.Join(", ", rulesBefore.Select(r => $"'{r}'"))}] when the " +
            "event was archived, so the late-rule premise does not hold");

        var queue = await CreateQueue(ctx);
        var (rule, ruleArn) = await PutRuleWithSqsTarget(ctx, bus, new JsonObject { ["source"] = new JsonArray(source) }, queue);
        var warmSeen = await SettleDeliveryPath(ctx, bus, source, queue.Url, consecutive: 2);
        var leakedLive = warmSeen.Where(b => NonceOf(b) == nonce).ToList();
        Ensure(
            leakedLive.Count == 0,
            $"the archived event '{nonce}' reached the late rule's queue BEFORE any replay, so it was " +
            $"delivered live to a rule that did not exist when it was put: [{string.Join(", ", leakedLive.Select(b => b?.ToJsonString()))}]");
        var drained = (await Collect(ctx, queue.Url, windowS: 10.0, maxTotal: 200)).Select(Body).ToList();
        Ensure(
            !drained.Any(b => NonceOf(b) == nonce),
            $"the archived event '{nonce}' reached the late rule's queue before the replay: [{string.Join(", ", drained.Select(b => b?.ToJsonString()))}]");

        var replay = XName("replay");
        await events.StartReplayAsync(new StartReplayRequest
        {
            ReplayName = replay,
            EventSourceArn = archiveArn,
            EventStartTime = eventTime.UtcDateTime - TimeSpan.FromMinutes(5),
            EventEndTime = eventTime.UtcDateTime + TimeSpan.FromMinutes(5),
            Destination = new ReplayDestination { Arn = busArn, FilterArns = [ruleArn] },
        });
        // EventBridge has no DeleteReplay, so a replay needs no teardown.
        var settled = await PollUntil(
            () => events.DescribeReplayAsync(new DescribeReplayRequest { ReplayName = replay }),
            d => d.State?.Value is "COMPLETED" or "FAILED" or "CANCELLED" or "CANCELLING",
            Repr,
            timeoutS: BudgetS(ctx, 400.0),
            intervalS: 1.0,
            description: $"replay {replay} reached a terminal state",
            ctx: ctx);
        Ensure(
            settled.State?.Value == "COMPLETED",
            $"replay {replay} settled at State='{settled.State?.Value}', expected 'COMPLETED': {Repr(settled)}");

        var (matched, others) = await WaitFor(ctx, queue.Url, b => NonceOf(b) == nonce, timeoutS: BudgetS(ctx, 200.0));
        if (matched.Count == 0 && BudgetSpent(ctx))
        {
            throw new TimeoutException($"the replayed event '{nonce}' on the queue: the run budget ended first; not graded");
        }

        // Anything else the replay re-emitted lands alongside the match; drain a fixed window for the
        // stray check.
        others.AddRange(await Collect(ctx, queue.Url, windowS: 8.0, maxTotal: 200));
        Ensure(
            matched.Count > 0,
            $"replay {replay} COMPLETED but the archived event '{nonce}' never reached rule '{rule}', " +
            "which was created after the event was archived. Messages seen instead: " +
            $"[{string.Join(", ", others.Select(m => Body(m)?.ToJsonString()))}]");
        var envelope = Body(matched[0]) as JsonObject;
        var replayName = (string?)envelope?["replay-name"];
        Ensure(
            replayName == replay,
            $"the redelivered event carries replay-name='{replayName}', expected " +
            $"'{replay}': {envelope?.ToJsonString()}");
        Ensure(
            JsonNode.DeepEquals(envelope?["detail"], detail),
            $"the redelivered event's detail is {envelope?["detail"]?.ToJsonString()}, the archived one was {detail.ToJsonString()}");
        var expectedTime = eventTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var time = envelope?["time"] is JsonValue tv && tv.TryGetValue<string>(out var ts) ? ts : null;
        Ensure(
            time == expectedTime,
            $"the redelivered event carries time='{time}'; a replay must keep the " +
            $"archived event's own time '{expectedTime}', not stamp the replay's: {envelope?.ToJsonString()}");
        var source2 = envelope?["source"] is JsonValue sv && sv.TryGetValue<string>(out var ss) ? ss : null;
        Ensure(source2 == source, $"the redelivered event carries source='{source2}', expected '{source}'");
        var strayReplays = others
            .Select(Body)
            .Where(b => b is JsonObject o && o.ContainsKey("replay-name") && NonceOf(b) != nonce)
            .ToList();
        Ensure(
            strayReplays.Count == 0,
            "the replay also re-emitted events outside its window (the current-time warm-ups): " +
            $"[{string.Join(", ", strayReplays.Select(b => b?.ToJsonString()))}]");
    }

    private sealed record Queue(string Name, string Url, string Arn);

    /// <summary>
    /// <c>json.dumps(obj)</c> for a flat object of strings: Python's <c>", "</c> and <c>": "</c> separators.
    /// </summary>
    private static string PyJson(JsonObject obj) =>
        "{" + string.Join(", ", obj.Select(kv => $"{JsonSerializer.Serialize(kv.Key)}: {kv.Value!.ToJsonString()}")) + "}";

    private static string Repr(PutEventsResponse r) =>
        $"{{'FailedEntryCount': {r.FailedEntryCount}, 'Entries': [{string.Join(", ", (r.Entries ?? []).Select(e => $"{{'EventId': '{e.EventId}', 'ErrorCode': '{e.ErrorCode}', 'ErrorMessage': '{e.ErrorMessage}'}}"))}]}}";

    private static string Repr(DescribeArchiveResponse d) =>
        $"{{'ArchiveName': '{d.ArchiveName}', 'State': '{d.State?.Value}', 'StateReason': '{d.StateReason}', 'EventCount': {d.EventCount}, 'SizeBytes': {d.SizeBytes}}}";

    private static string Repr(DescribeReplayResponse d) =>
        $"{{'ReplayName': '{d.ReplayName}', 'State': '{d.State?.Value}', 'StateReason': '{d.StateReason}', 'Destination': {Repr(d.Destination)}}}";

    private static string Repr(ReplayDestination? d) =>
        d is null ? "None" : $"{{'Arn': '{d.Arn}', 'FilterArns': [{string.Join(", ", (d.FilterArns ?? []).Select(a => $"'{a}'"))}]}}";

    private static Dictionary<string, string> XsvcResourceTags() =>
        new() { [ManagedTagKey] = "true", ["sim2real:prefix"] = XsvcPrefix.TrimEnd('-') };

    private static List<Tag> XsvcTagsKv() =>
        [.. XsvcResourceTags().Select(kv => new Tag { Key = kv.Key, Value = kv.Value })];

    /// <summary>
    /// A unique, prefix-tagged resource name. ArchiveName is capped at 48 characters by real AWS,
    /// so kinds stay short.
    /// </summary>
    private static string XName(string kind) => FidelityContext.UniqueName($"{XsvcPrefix}{kind}-");

    private static string? NonceOf(JsonNode? body) =>
        body is JsonObject o && o["detail"] is JsonObject d && d["nonce"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>
    /// A poll's timeout: cap_s, bounded by the runner's remaining time less 5 s so the poll fails
    /// with its own message.
    /// </summary>
    private static double BudgetS(FidelityContext ctx, double capS) => Math.Max(1.0, Math.Min(capS, ctx.TimeLeftSeconds - 5.0));

    /// <summary>True when the runner's remaining time, not the wait's own deadline, ended a capped wait.</summary>
    private static bool BudgetSpent(FidelityContext ctx) => ctx.TimeLeftSeconds <= 6.0;

    /// <summary>Poll probe() until predicate(result) is truthy; return the final result (raises on timeout).</summary>
    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, Func<T, string> repr,
        double timeoutS = 60.0, double intervalS = 1.0, string description = "condition", FidelityContext? ctx = null)
        where T : class
    {
        var started = FidelityTime.StartNew();
        var deadline = TimeSpan.FromSeconds(timeoutS);
        T? last = null;
        while (started.Elapsed < deadline)
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }

            var remaining = deadline - started.Elapsed;
            await FidelityTime.Delay(TimeSpan.FromSeconds(Math.Max(0.0, Math.Min(intervalS, remaining.TotalSeconds))));
        }

        if (ctx is not null && BudgetSpent(ctx))
        {
            throw new TimeoutException($"{description}: the run budget ended first; not graded");
        }

        throw Fail($"{description} not met within {timeoutS}s. Last result: {(last is null ? "None" : repr(last))}");
    }

    private static async Task<List<string>> ListRuleNames(Amazon.EventBridge.IAmazonEventBridge events, string bus)
    {
        var names = new List<string>();
        string? token = null;
        do
        {
            var page = await events.ListRulesAsync(new ListRulesRequest { EventBusName = bus, NextToken = token });
            names.AddRange((page.Rules ?? []).Select(r => r.Name));
            token = page.NextToken;
        }
        while (!string.IsNullOrEmpty(token));

        return names;
    }

    private static async Task<DescribeArchiveResponse> WaitArchiveEnabled(Amazon.EventBridge.IAmazonEventBridge events, string name, double timeoutS = 300.0, FidelityContext? ctx = null)
    {
        var d = await PollUntil(
            () => events.DescribeArchiveAsync(new DescribeArchiveRequest { ArchiveName = name }),
            d => d.State?.Value is "ENABLED" or "CREATE_FAILED" or "DISABLED",
            Repr,
            timeoutS: timeoutS,
            intervalS: 1.0,
            description: $"archive {name} left CREATING",
            ctx: ctx);
        Ensure(d.State?.Value == "ENABLED", $"archive {name} settled at State='{d.State?.Value}': {Repr(d)}");
        return d;
    }

    /// <summary>
    /// Delete an archive and wait for it to be gone: the bus cannot be deleted while an archive
    /// still references it.
    /// </summary>
    private static async Task DeleteArchiveAndWait(FidelityContext ctx, string name, double timeoutS = 180.0)
    {
        var events = ctx.Events;
        var error = await FidelityContext.CaptureError(() => events.DeleteArchiveAsync(new DeleteArchiveRequest { ArchiveName = name }));
        if (error is not null && error.Code != "ResourceNotFoundException")
        {
            throw new InvalidOperationException($"could not delete archive {name}: {error}", error.Exception);
        }

        var started = FidelityTime.StartNew();
        while (started.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var describeError = await FidelityContext.CaptureError(() => events.DescribeArchiveAsync(new DescribeArchiveRequest { ArchiveName = name }));
            if (describeError?.Code == "ResourceNotFoundException")
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(3));
        }

        throw new InvalidOperationException($"archive {name} still present {timeoutS}s after DeleteArchive");
    }

    /// <summary>Create an SQS queue (registers delete_queue teardown). Returns {name,url,arn}.</summary>
    private static async Task<Queue> CreateQueue(FidelityContext ctx)
    {
        var name = XName("q");
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Tags = XsvcResourceTags() })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes["QueueArn"];
        return new Queue(name, url, arn);
    }

    /// <summary>
    /// A rule on `bus` with `pattern`, targeting `queue`, with a SourceArn-scoped queue policy.
    /// Teardown is registered so targets are removed before the rule is deleted (LIFO).
    /// </summary>
    private static async Task<(string Rule, string RuleArn)> PutRuleWithSqsTarget(FidelityContext ctx, string bus, JsonObject pattern, Queue queue)
    {
        var events = ctx.Events;
        var sqs = ctx.Sqs;
        var rule = XName("rule");
        var ruleArn = (await events.PutRuleAsync(new PutRuleRequest
        {
            Name = rule,
            EventBusName = bus,
            EventPattern = pattern.ToJsonString(),
            State = "ENABLED",
            Tags = XsvcTagsKv(),
        })).RuleArn;
        ctx.OnCleanup(() => events.DeleteRuleAsync(new DeleteRuleRequest { Name = rule, EventBusName = bus, Force = true }));
        var policy = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Sid"] = "AllowEventBridgeSend",
                ["Effect"] = "Allow",
                ["Principal"] = new JsonObject { ["Service"] = "events.amazonaws.com" },
                ["Action"] = "sqs:SendMessage",
                ["Resource"] = queue.Arn,
                ["Condition"] = new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = ruleArn } },
            }),
        };
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = queue.Url, Attributes = new() { ["Policy"] = policy.ToJsonString() } });
        var put = await events.PutTargetsAsync(new PutTargetsRequest { Rule = rule, EventBusName = bus, Targets = [new Target { Id = "q", Arn = queue.Arn }] });
        ctx.OnCleanup(() => events.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = rule, EventBusName = bus, Ids = ["q"], Force = true }));
        Ensure(put.FailedEntryCount == 0, $"PutTargets rejected the SQS target: {{'FailedEntryCount': {put.FailedEntryCount}}}");
        return (rule, ruleArn);
    }

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

    /// <summary>Drain a queue over a bounded window and return EVERY message seen (deleting each).</summary>
    private static async Task<List<SqsMessage>> Collect(FidelityContext ctx, string queueUrl, double windowS = 30.0, int maxTotal = 10)
    {
        var sqs = ctx.Sqs;
        var seen = new List<SqsMessage>();
        var started = FidelityTime.StartNew();
        var window = TimeSpan.FromSeconds(windowS);
        while (started.Elapsed < window && seen.Count < maxTotal)
        {
            var wait = Math.Min(5, Math.Max(1, (int)(window - started.Elapsed).TotalSeconds));
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = wait });
            foreach (var msg in resp.Messages ?? [])
            {
                seen.Add(msg);
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
            }
        }

        return seen;
    }

    /// <summary>Poll a queue until a batch holds a message matching predicate(), deleting every one seen.</summary>
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
            var wait = Math.Min(2, Math.Max(0, (int)(deadline - started.Elapsed).TotalSeconds));
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = wait });
            foreach (var msg in resp.Messages ?? [])
            {
                (predicate(Body(msg)) ? matched : others).Add(msg);
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
            }
        }

        return (matched, others);
    }

    /// <summary>
    /// Emit single current-time control events until `consecutive` round-trip back to back; return
    /// every body seen on the queue meanwhile. A fresh rule becomes effective progressively and a dropped
    /// event is never retried, so this polls the control's own arrival rather than sleeping.
    /// </summary>
    private static async Task<List<JsonNode?>> SettleDeliveryPath(FidelityContext ctx, string bus, string source, string queueUrl, int consecutive = 2)
    {
        var events = ctx.Events;
        var streak = 0;
        var seen = new List<JsonNode?>();
        for (var i = 0; i < 30; i++)
        {
            var n = XName("warm");
            await events.PutEventsAsync(new PutEventsRequest
            {
                Entries =
                [
                    new PutEventsRequestEntry
                    {
                        Source = source,
                        DetailType = "Warmup",
                        Detail = PyJson(new JsonObject { ["nonce"] = n }),
                        EventBusName = bus,
                    },
                ],
            });
            var (got, rest) = await WaitFor(ctx, queueUrl, b => NonceOf(b) == n, timeoutS: 15.0);
            seen.AddRange(got.Concat(rest).Select(Body));
            streak = got.Count > 0 ? streak + 1 : 0;
            if (streak >= consecutive)
            {
                return seen;
            }
        }

        throw Fail(
            $"the rule -> {queueUrl} delivery path never round-tripped {consecutive} consecutive control " +
            $"events (longest streak {streak})");
    }
}

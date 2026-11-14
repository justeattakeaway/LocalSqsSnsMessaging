// Translated from behavioral/events__sqs/check_events_archive_replay_does_not_redeliver_to_a_rule_outside_filter_arns.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.EventBridge.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using SqsMessage = Amazon.SQS.Model.Message;
using Tag = Amazon.EventBridge.Model.Tag;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.EventsSqs;

/// <summary>events__sqs.events-archive-replay-does-not-redeliver-to-a-rule-outside-filter-arns</summary>
[Category("AwsEvaluationSuite")]
public sealed class EventsArchiveReplayDoesNotRedeliverToARuleOutsideFilterArns
{
    private const string XsvcPrefix = "sim2real-xsvc-";
    private const string ManagedTagKey = "sim2real:managed";

    /// <summary>
    /// A replay whose Destination.FilterArns names rule A re-emits to A only.
    ///
    /// Rule B, matching the same event and proven live, receives nothing from the replay.
    /// </summary>
    [Test]
    [DisplayName("events__sqs.events-archive-replay-does-not-redeliver-to-a-rule-outside-filter-arns")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var source = XName("src").Replace('-', '.');
        var pattern = new JsonObject { ["source"] = new JsonArray(source) };

        var bus = XName("bus");
        var busArn = (await events.CreateEventBusAsync(new CreateEventBusRequest { Name = bus, Tags = XsvcTagsKv() })).EventBusArn;
        ctx.OnCleanup(() => ctx.Events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));

        var queueA = await CreateQueue(ctx);
        var (ruleA, ruleAArn) = await PutRuleWithSqsTarget(ctx, bus, pattern, queueA);
        var queueB = await CreateQueue(ctx);
        var (ruleB, ruleBArn) = await PutRuleWithSqsTarget(ctx, bus, pattern, queueB);
        Ensure(ruleAArn != ruleBArn, $"two rules share an ARN: '{ruleAArn}'");

        // Settle both delivery paths BEFORE the archive exists, so no warm-up is archived.
        await SettleDeliveryPath(ctx, bus, source, queueA.Url, consecutive: 2);
        await SettleDeliveryPath(ctx, bus, source, queueB.Url, consecutive: 2);

        var archive = XName("arc");
        var archiveArn = (await events.CreateArchiveAsync(new CreateArchiveRequest
        {
            ArchiveName = archive,
            EventSourceArn = busArn,
            EventPattern = pattern.ToJsonString(),
        })).ArchiveArn;
        ctx.OnCleanup(() => DeleteArchiveAndWait(ctx, archive));
        await WaitArchiveEnabled(events, archive);
        await Collect(ctx, queueA.Url, windowS: 8.0, maxTotal: 200);
        await Collect(ctx, queueB.Url, windowS: 8.0, maxTotal: 200);

        var now = FidelityTime.UtcNow.AddHours(-1);
        var eventTime = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, TimeSpan.Zero);
        var nonce = XName("n");
        var detail = new JsonObject { ["nonce"] = nonce, ["leg"] = "archived" };
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
            $"PutEvents did not accept the entry: {Repr(resp)}");

        // Positive control 1: the LIVE event reaches BOTH rules.
        foreach (var (label, q) in new[] { ("A", queueA), ("B", queueB) })
        {
            var (got, _) = await WaitFor(ctx, q.Url, b => NonceOf(b) == nonce, timeoutS: 180.0);
            Ensure(got.Count > 0, $"the live event '{nonce}' never reached rule {label}'s queue, so B's silence later proves nothing");
            var live = Body(got[0]);
            Ensure(
                !(live is JsonObject lo ? lo.ContainsKey("replay-name") : live?.GetValueKind() == JsonValueKind.String && live.GetValue<string>().Contains("replay-name", StringComparison.Ordinal)),
                $"a LIVE delivery to rule {label} carried replay-name: {live?.ToJsonString()}");
        }

        var ingested = await PollUntil(
            () => events.DescribeArchiveAsync(new DescribeArchiveRequest { ArchiveName = archive }),
            d => (d.EventCount ?? 0) >= 1,
            Repr,
            timeoutS: 1500.0,
            intervalS: 10.0,
            description:
                $"archive {archive} ingested the published event (real AWS measured 151 s to beyond " +
                "900 s for this lag; AWS documents no bound)");
        Ensure((ingested.SizeBytes ?? 0) > 0, $"archive counted the event but stored nothing: {Repr(ingested)}");

        // Every message counted from here on came from the replay (or the post-control).
        await Collect(ctx, queueA.Url, windowS: 10.0, maxTotal: 200);
        await Collect(ctx, queueB.Url, windowS: 10.0, maxTotal: 200);

        var replay = XName("replay");
        await events.StartReplayAsync(new StartReplayRequest
        {
            ReplayName = replay,
            EventSourceArn = archiveArn,
            EventStartTime = eventTime.UtcDateTime - TimeSpan.FromMinutes(5),
            EventEndTime = eventTime.UtcDateTime + TimeSpan.FromMinutes(5),
            Destination = new ReplayDestination { Arn = busArn, FilterArns = [ruleAArn] },
        });
        // No DeleteReplay API exists, so the replay job cannot be cleaned up.
        var settled = await PollUntil(
            () => events.DescribeReplayAsync(new DescribeReplayRequest { ReplayName = replay }),
            d => d.State?.Value is "COMPLETED" or "FAILED" or "CANCELLED" or "CANCELLING",
            Repr,
            timeoutS: 400.0,
            intervalS: 5.0,
            description: $"replay {replay} reached a terminal state");
        Ensure(settled.State?.Value == "COMPLETED", $"replay {replay} settled at '{settled.State?.Value}': {Repr(settled)}");
        var filterArns = settled.Destination?.FilterArns;
        Ensure(
            filterArns is not null && filterArns.SequenceEqual([ruleAArn]),
            $"DescribeReplay reports FilterArns={Repr(settled.Destination)}, expected only '{ruleAArn}'");

        // Positive control 2 (and the barrier): the replay reaches A.
        var (gotA, _) = await WaitFor(ctx, queueA.Url, b => NonceOf(b) == nonce, timeoutS: 200.0);
        Ensure(gotA.Count > 0, $"replay {replay} COMPLETED but never reached rule A ({ruleA}), the rule it named");
        var replayed = Body(gotA[0]);
        var replayName = replayed is JsonObject ro ? (string?)ro["replay-name"] : null;
        Ensure(replayName == replay, $"rule A's redelivery carries replay-name='{replayName}', expected '{replay}'");

        // A's receipt of the replay is the barrier that anchors B's hold window.
        var held = (await Collect(ctx, queueB.Url, windowS: 60.0, maxTotal: 200)).Select(Body).ToList();
        Ensure(
            held.Count == 0,
            $"rule B ({ruleB}) is not in the replay's FilterArns, yet its queue received {held.Count} " +
            $"message(s) after the replay reached rule A: [{string.Join(", ", held.Select(h => h?.ToJsonString()))}]");

        // Positive control 3: B's path is still live right after the hold.
        var post = XName("post");
        await events.PutEventsAsync(new PutEventsRequest
        {
            Entries =
            [
                new PutEventsRequestEntry
                {
                    Source = source,
                    DetailType = "PostControl",
                    Detail = PyJson(new JsonObject { ["nonce"] = post }),
                    EventBusName = bus,
                },
            ],
        });
        var (gotB, _) = await WaitFor(ctx, queueB.Url, b => NonceOf(b) == post, timeoutS: 180.0);
        Ensure(
            gotB.Count > 0,
            "a fresh live event never reached rule B after the hold, so B's silence during the replay " +
            "may be a dead path rather than FilterArns");
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

    /// <summary>Poll probe() until predicate(result) is truthy; return the final result (raises on timeout).</summary>
    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, Func<T, string> repr,
        double timeoutS = 60.0, double intervalS = 1.0, string description = "condition")
        where T : class
    {
        var started = FidelityTime.StartNew();
        T? last = null;
        while (started.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"{description} not met within {timeoutS}s. Last result: {(last is null ? "None" : repr(last))}");
    }

    private static async Task<DescribeArchiveResponse> WaitArchiveEnabled(Amazon.EventBridge.IAmazonEventBridge events, string name)
    {
        var d = await PollUntil(
            () => events.DescribeArchiveAsync(new DescribeArchiveRequest { ArchiveName = name }),
            d => d.State?.Value is "ENABLED" or "CREATE_FAILED" or "DISABLED",
            Repr,
            timeoutS: 300.0,
            intervalS: 3.0,
            description: $"archive {name} left CREATING");
        Ensure(d.State?.Value == "ENABLED", $"archive {name} settled at State='{d.State?.Value}': {Repr(d)}");
        return d;
    }

    /// <summary>
    /// Delete an archive and WAIT for it to be gone: the bus cannot be deleted while an archive still
    /// references it.
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

    /// <summary>Poll a queue until a message whose parsed body satisfies predicate() arrives.</summary>
    private static async Task<(List<SqsMessage> Matched, List<SqsMessage> Others)> WaitFor(
        FidelityContext ctx, string queueUrl, Func<JsonNode?, bool> predicate, double timeoutS = 60.0)
    {
        var matched = new List<SqsMessage>();
        var others = new List<SqsMessage>();
        var started = FidelityTime.StartNew();
        while (started.Elapsed < TimeSpan.FromSeconds(timeoutS) && matched.Count == 0)
        {
            foreach (var msg in await Collect(ctx, queueUrl, windowS: Math.Min(8.0, timeoutS), maxTotal: 200))
            {
                (predicate(Body(msg)) ? matched : others).Add(msg);
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

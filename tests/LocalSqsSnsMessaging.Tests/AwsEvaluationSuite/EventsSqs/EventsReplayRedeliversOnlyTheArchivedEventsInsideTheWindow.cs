// Translated from behavioral/events__sqs/check_events_replay_redelivers_only_the_archived_events_inside_the_window.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using SqsMessage = Amazon.SQS.Model.Message;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.EventsSqs;

/// <summary>events__sqs.events-replay-redelivers-only-the-archived-events-inside-the-window</summary>
[Category("AwsEvaluationSuite")]
public sealed class EventsReplayRedeliversOnlyTheArchivedEventsInsideTheWindow
{
    private const string XsvcPrefix = "sim2real-xsvc-";
    private const string ManagedTagKey = "sim2real:managed";

    /// <summary>
    /// StartReplay re-emits exactly the archived events whose Time falls inside the window, each
    /// redelivery carrying this replay's replay-name, and excludes an archived event outside it.
    /// </summary>
    /// <remarks>Reference: https://docs.aws.amazon.com/eventbridge/latest/userguide/eb-archive.html</remarks>
    [Test]
    [DisplayName("events__sqs.events-replay-redelivers-only-the-archived-events-inside-the-window")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var source = XName("src").Replace('-', '.');

        var bus = XName("bus");
        var busArn = (await events.CreateEventBusAsync(new CreateEventBusRequest { Name = bus, Tags = XsvcTagsKv() })).EventBusArn;
        ctx.OnCleanup(() => ctx.Events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));

        var queue = await CreateQueue(ctx);
        var (rule, ruleArn) = await PutRuleWithSqsTarget(ctx, new JsonObject { ["source"] = new JsonArray(source) }, queue, bus: bus);

        // Settle the delivery path BEFORE the archive exists, so the warm-up traffic is never archived and
        // cannot pollute the replay window. A fresh rule drops events until it goes live and EventBridge
        // never retries a drop, so a cold path would make the replay's single delivery unprovable.
        await SettleDeliveryPath(ctx, bus, source, queue.Url, consecutive: 2);

        var archive = XName("arc");
        var archiveArn = (await events.CreateArchiveAsync(new CreateArchiveRequest { ArchiveName = archive, EventSourceArn = busArn })).ArchiveArn;
        ctx.OnCleanup(() => DeleteArchiveAndWait(ctx, archive));
        var enabled = await PollUntil(
            () => events.DescribeArchiveAsync(new DescribeArchiveRequest { ArchiveName = archive }),
            d => d.State?.Value is "ENABLED" or "CREATE_FAILED" or "DISABLED",
            ArchiveRepr,
            timeoutS: 300.0,
            intervalS: 3.0,
            description: $"archive {archive} left CREATING");
        Ensure(
            enabled.State?.Value == "ENABLED",
            $"archive {archive} settled at State='{enabled.State?.Value}', expected 'ENABLED': {ArchiveRepr(enabled)}");
        await Collect(ctx, queue.Url, windowS: 10.0, maxTotal: 200);

        var now = FidelityTime.UtcNow.UtcDateTime;
        var insideTime = now - TimeSpan.FromHours(1);
        var outsideTime = now - TimeSpan.FromHours(2);
        var nonceA = XName("n-a");
        var nonceB = XName("n-b");
        var nonceC = XName("n-c");
        var details = new Dictionary<string, JsonObject>
        {
            [nonceA] = new() { ["nonce"] = nonceA, ["leg"] = "inside", ["n"] = 1 },
            [nonceB] = new() { ["nonce"] = nonceB, ["leg"] = "inside", ["n"] = 2 },
            [nonceC] = new() { ["nonce"] = nonceC, ["leg"] = "outside", ["n"] = 3 },
        };
        // Windowing is decided by an explicit Time on each entry, not by racing the wall clock.
        var resp = await events.PutEventsAsync(new PutEventsRequest
        {
            Entries =
            [
                new() { Time = insideTime, Source = source, DetailType = "Replayable", Detail = details[nonceA].ToJsonString(), EventBusName = bus },
                new() { Time = insideTime, Source = source, DetailType = "Replayable", Detail = details[nonceB].ToJsonString(), EventBusName = bus },
                new() { Time = outsideTime, Source = source, DetailType = "Replayable", Detail = details[nonceC].ToJsonString(), EventBusName = bus },
            ],
        });
        Ensure(
            resp.FailedEntryCount == 0 && (resp.Entries ?? []).Count == 3,
            "PutEvents did not accept all three entries, so nothing below is attributable to the " +
            $"replay's window selection: {{'FailedEntryCount': {resp.FailedEntryCount}, 'Entries': " +
            $"[{string.Join(", ", (resp.Entries ?? []).Select(e => $"{{'EventId': '{e.EventId}', 'ErrorCode': '{e.ErrorCode}'}}"))}]}}");

        // The live delivery of all three happens now, and it must NOT look like a replay. Draining it
        // here is also what makes every message counted after StartReplay unambiguously a redelivery.
        var live = new Dictionary<string, JsonObject>();
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(180.0) && live.Count < 3)
        {
            foreach (var msg in await Collect(ctx, queue.Url, windowS: 10.0, maxTotal: 200))
            {
                if (Body(msg) is JsonObject body && body["detail"] is JsonObject detail)
                {
                    var n = Str(detail, "nonce");
                    if (n is not null && details.ContainsKey(n))
                    {
                        live[n] = body;
                    }
                }
            }
        }

        Ensure(
            live.Keys.ToHashSet().SetEquals(details.Keys),
            "the three published events were not all delivered live, so the queue's reachability at " +
            $"replay time is not established: got {Repr(live.Keys.Order(StringComparer.Ordinal))}, expected {Repr(details.Keys.Order(StringComparer.Ordinal))}");
        var replayMarked = live.Where(kv => kv.Value.ContainsKey("replay-name")).ToList();
        Ensure(
            replayMarked.Count == 0,
            "a LIVE delivery carried a replay-name field, which would make every redelivery assertion " +
            $"below unfalsifiable: {{{string.Join(", ", replayMarked.Select(kv => $"'{kv.Key}': {PyRepr(kv.Value)}"))}}}");

        // Archive EventCount lags PutEvents by minutes, with no documented bound.
        var ingested = await PollUntil(
            () => events.DescribeArchiveAsync(new DescribeArchiveRequest { ArchiveName = archive }),
            d => (d.EventCount ?? 0) >= 3,
            ArchiveRepr,
            timeoutS: 1500.0,
            intervalS: 10.0,
            description:
                $"archive {archive} ingested all 3 published events (measured on real AWS across four " +
                "runs: 151 s, 353 s, 559 s and once beyond 900 s -- AWS documents no bound, so this " +
                "poll is generous and FAILS on timeout rather than skipping)");
        Ensure(
            (ingested.SizeBytes ?? 0) > 0,
            $"the archive counted 3 events but reports SizeBytes={(ingested.SizeBytes is { } sb ? sb.ToString(CultureInfo.InvariantCulture) : "None")}: nothing " +
            "was stored, so a replay could not re-emit anything");

        // Empty the queue immediately before the replay: every message counted from here came from it.
        await Collect(ctx, queue.Url, windowS: 15.0, maxTotal: 200);

        var replay = XName("replay");
        await events.StartReplayAsync(new StartReplayRequest
        {
            ReplayName = replay,
            EventSourceArn = archiveArn,
            EventStartTime = insideTime - TimeSpan.FromMinutes(5),
            EventEndTime = insideTime + TimeSpan.FromMinutes(5),
            Destination = new ReplayDestination { Arn = busArn, FilterArns = [ruleArn] },
        });
        // EventBridge exposes no DeleteReplay, so there is no teardown to register; a replay ages out.
        var settled = await PollUntil(
            () => events.DescribeReplayAsync(new DescribeReplayRequest { ReplayName = replay }),
            d => d.State?.Value is "COMPLETED" or "FAILED" or "CANCELLED" or "CANCELLING",
            ReplayRepr,
            timeoutS: 400.0,
            intervalS: 5.0,
            description: $"replay {replay} reached a terminal state");
        Ensure(
            settled.State?.Value == "COMPLETED",
            $"replay {replay} settled at State='{settled.State?.Value}', expected 'COMPLETED'; the whole " +
            $"replay was {ReplayRepr(settled)}");

        var redelivered = new Dictionary<string, JsonObject>();
        var strangers = new List<JsonNode?>();
        deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(200.0) && redelivered.Count < 2)
        {
            foreach (var msg in await Collect(ctx, queue.Url, windowS: 15.0, maxTotal: 200))
            {
                var body = Body(msg);
                if (!(body is JsonObject bo && bo["detail"] is JsonObject detail))
                {
                    strangers.Add(body);
                    continue;
                }

                var n = Str(detail, "nonce");
                if (n is not null && details.ContainsKey(n))
                {
                    redelivered[n] = bo;
                }
                else
                {
                    strangers.Add(body);
                }
            }
        }

        Ensure(
            redelivered.Keys.ToHashSet().SetEquals([nonceA, nonceB]),
            $"the replay over [{IsoFormat(insideTime - TimeSpan.FromMinutes(5))}, " +
            $"{IsoFormat(insideTime + TimeSpan.FromMinutes(5))}] redelivered " +
            $"{Repr(redelivered.Keys.Order(StringComparer.Ordinal))}, expected exactly {Repr(new[] { nonceA, nonceB }.Order(StringComparer.Ordinal))}. " +
            $"'{nonceC}' was archived with a Time two hours in the past, outside that window, so its " +
            "presence would mean the window is not applied and its siblings' absence would mean the " +
            "replay re-emitted nothing");
        Ensure(
            strangers.Count == 0,
            $"the replay delivered {strangers.Count} message(s) carrying none of this check's nonces, so " +
            $"the set equality above was measured against contaminated traffic: [{string.Join(", ", strangers.Select(PyRepr))}]");
        foreach (var (n, envelope) in redelivered.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            Ensure(
                Str(envelope, "replay-name") == replay,
                $"redelivered event '{n}' carries replay-name={PyRepr(envelope["replay-name"])}, expected " +
                $"'{replay}' -- without that field it is indistinguishable from a leftover live " +
                $"delivery: {PyRepr(envelope)}");
            Ensure(
                JsonNode.DeepEquals(envelope["detail"], details[n]),
                $"redelivered event '{n}' does not carry the payload that was archived: got " +
                $"{PyRepr(envelope["detail"])}, published {PyRepr(details[n])}");
            Ensure(
                Str(envelope, "source") == source,
                $"redelivered event '{n}' carries source={PyRepr(envelope["source"])}, expected '{source}'");
            Ensure(
                Str(envelope, "detail-type") == "Replayable",
                $"redelivered event '{n}' carries detail-type={PyRepr(envelope["detail-type"])}, expected " +
                "'Replayable'");
        }

        Ensure(
            (await ListRuleNames(events, bus)).Contains(rule),
            $"rule '{rule}' is absent from ListRules on bus {bus}, so the FilterArns the replay was " +
            "routed through no longer resolves");
    }

    private static string? Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static string Repr(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(i => $"'{i}'")) + "]";

    /// <summary>datetime.isoformat() of a UTC-aware datetime.</summary>
    private static string IsoFormat(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'+00:00'", CultureInfo.InvariantCulture);

    private static string ArchiveRepr(DescribeArchiveResponse d) =>
        $"{{'ArchiveName': '{d.ArchiveName}', 'State': '{d.State?.Value}', 'EventCount': {d.EventCount}, 'SizeBytes': {d.SizeBytes}}}";

    private static string ReplayRepr(DescribeReplayResponse d) =>
        $"{{'ReplayName': '{d.ReplayName}', 'State': '{d.State?.Value}', 'StateReason': '{d.StateReason}'}}";

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

    private sealed record Queue(string Name, string Url, string Arn);

    private static Dictionary<string, string> XsvcResourceTags() =>
        new() { [ManagedTagKey] = "true", ["sim2real:prefix"] = XsvcPrefix.TrimEnd('-') };

    private static List<Amazon.EventBridge.Model.Tag> XsvcTagsKv() =>
        [.. XsvcResourceTags().Select(kv => new Amazon.EventBridge.Model.Tag { Key = kv.Key, Value = kv.Value })];

    /// <summary>A unique, prefix-tagged resource name: sim2real-xsvc-&lt;kind&gt;-&lt;uuid&gt;.</summary>
    /// <remarks>ArchiveName is capped at 48 characters by real AWS, so archive kinds stay short.</remarks>
    private static string XName(string kind) => FidelityContext.UniqueName($"{XsvcPrefix}{kind}-");

    /// <summary>Every rule name on `bus`, via the paginator ListRules does have.</summary>
    private static async Task<List<string>> ListRuleNames(AmazonEventBridgeClient events, string bus)
    {
        var names = new List<string>();
        string? token = null;
        do
        {
            var page = await events.ListRulesAsync(new ListRulesRequest { EventBusName = bus, NextToken = token });
            names.AddRange((page.Rules ?? []).Select(r => r.Name));
            token = page.NextToken;
        }
        while (token is not null);

        return names;
    }

    /// <summary>
    /// Delete an archive and wait for it to be gone; the bus cannot be deleted while an archive
    /// still references it. Reports failures on stderr instead of raising.
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

        throw new InvalidOperationException($"archive {name} still present {timeoutS}s after DeleteArchive; its event bus may fail to delete");
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

    /// <summary>Drain a queue over a bounded window with long-polling ReceiveMessage; return every message.</summary>
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

    /// <summary>Emit single control events until `consecutive` of them round-trip back to back.</summary>
    /// <remarks>
    /// A rule and its target become effective progressively and a dropped event is never retried, so
    /// one arrival proves only that some path is live.
    /// </remarks>
    private static async Task SettleDeliveryPath(FidelityContext ctx, string bus, string source, string queueUrl, int consecutive = 2)
    {
        var events = ctx.Events;
        var streak = 0;
        for (var i = 0; i < 30; i++)
        {
            var nonce = XName("warm");
            await events.PutEventsAsync(new PutEventsRequest
            {
                Entries = [new() { Source = source, DetailType = "Warmup", Detail = $$"""{"nonce": "{{nonce}}"}""", EventBusName = bus }],
            });
            var (got, _) = await WaitFor(
                ctx,
                queueUrl,
                b => b is JsonObject o && o["detail"] is JsonObject d && Str(d, "nonce") == nonce,
                timeoutS: 15.0);
            streak = got.Count > 0 ? streak + 1 : 0;
            if (streak >= consecutive)
            {
                return;
            }
        }

        throw Fail(
            $"the rule -> {queueUrl} delivery path never round-tripped {consecutive} consecutive " +
            "control events, so a replay's redelivery could not be distinguished from a cold path " +
            $"dropping it (longest streak reached: {streak})");
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

    /// <summary>Create a rule for `pattern` on `bus` targeting `queue`, granting it SendMessage.</summary>
    /// <remarks>Returns (rule name, rule ARN); the ARN is what a replay's FilterArns needs.</remarks>
    private static async Task<(string Name, string Arn)> PutRuleWithSqsTarget(FidelityContext ctx, JsonObject pattern, Queue queue, string bus)
    {
        var events = ctx.Events;
        var name = XName("rule");
        var ruleArn = (await events.PutRuleAsync(new PutRuleRequest { Name = name, EventBusName = bus, EventPattern = pattern.ToJsonString() })).RuleArn;
        ctx.OnCleanup(() => ctx.Events.DeleteRuleAsync(new DeleteRuleRequest { Name = name, EventBusName = bus, Force = true }));
        await SetQueuePolicy(ctx, queue.Url, AllowServiceSend(queue.Arn, "events.amazonaws.com", ruleArn));
        await events.PutTargetsAsync(new PutTargetsRequest { Rule = name, EventBusName = bus, Targets = [new Target { Id = "t1", Arn = queue.Arn }] });
        ctx.OnCleanup(() => ctx.Events.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = name, EventBusName = bus, Ids = ["t1"], Force = true }));
        return (name, ruleArn);
    }

    /// <summary>Poll probe() until predicate(result) is truthy; return the final result (raises on timeout).</summary>
    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, Func<T, string> repr, double timeoutS = 60.0, double intervalS = 1.0, string description = "condition")
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

        throw Fail($"{description} not met within {timeoutS.ToString("0.0", CultureInfo.InvariantCulture)}s. Last result: {(last is null ? "None" : repr(last))}");
    }
}

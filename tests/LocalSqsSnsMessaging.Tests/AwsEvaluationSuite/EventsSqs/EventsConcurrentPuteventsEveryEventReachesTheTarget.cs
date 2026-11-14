// Translated from behavioral/events__sqs/check_events_concurrent_putevents_every_event_reaches_the_target.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.EventBridge.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using SqsMessage = Amazon.SQS.Model.Message;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.EventsSqs;

/// <summary>events__sqs.events-concurrent-putevents-every-event-reaches-the-target</summary>
[Category("AwsEvaluationSuite")]
public sealed class EventsConcurrentPuteventsEveryEventReachesTheTarget
{
    private const string XsvcPrefix = "sim2real-xsvc-";
    private const string ManagedTagKey = "sim2real:managed";

    /// <summary>
    /// Forty events put by eight concurrent callers all reach the rule's SQS target: the delivered
    /// nonces equal the published ones, with duplicates counted since delivery is at-least-once.
    /// </summary>
    [Test]
    [DisplayName("events__sqs.events-concurrent-putevents-every-event-reaches-the-target")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var source = XName("src").Replace('-', '.');
        var queue = await CreateQueue(ctx);
        var rule = await PutRuleWithSqsTarget(ctx, new JsonObject { ["source"] = new JsonArray(source) }, queue);

        // A freshly created rule/target silently drops events until the path goes live, and EventBridge
        // never retries a dropped event -- so the path has to be proven live BEFORE the load, or a
        // shortfall afterwards is indistinguishable from warm-up. One delivered control is not enough
        // (a burst issued right after the first control arrives can still lose events), so require
        // THREE consecutive single-event round trips before publishing anything that counts.
        await SettleDeliveryPath(ctx, source, queue.Url, consecutive: 3);
        await Collect(ctx, queue.Url, windowS: 5.0, maxTotal: 100);

        var published = new HashSet<string>(StringComparer.Ordinal);
        var batches = new List<List<string>>();
        for (var i = 0; i < 8; i++)
        {
            var batch = Enumerable.Range(0, 5).Select(_ => XName("n")).ToList();
            batches.Add(batch);
            published.UnionWith(batch);
        }

        Ensure(published.Count == 40, $"nonce minting collided: {published.Count} unique of 40: {SortedRepr(published)}");

        Task<PutEventsResponse> Publish(List<string> batch)
        {
            var client = ctx.Events;
            return client.PutEventsAsync(new PutEventsRequest
            {
                Entries = [.. batch.Select(n => new PutEventsRequestEntry { Source = source, DetailType = "Load", Detail = $$"""{"nonce": "{{n}}"}""" })],
            });
        }

        var responses = await Task.WhenAll(batches.Select(b => Task.Run(() => Publish(b))));

        var eventIds = new List<string>();
        for (var i = 0; i < responses.Length; i++)
        {
            var resp = responses[i];
            var entries = resp.Entries ?? [];
            Ensure(
                resp.FailedEntryCount == 0,
                $"caller {i} had partial failures: FailedEntryCount=" +
                $"{resp.FailedEntryCount} Entries=[{string.Join(", ", entries.Select(Repr))}]");
            Ensure(entries.Count == 5, $"caller {i} expected 5 result entries, got [{string.Join(", ", entries.Select(Repr))}]");
            foreach (var e in entries)
            {
                Ensure(!string.IsNullOrEmpty(e.EventId), $"caller {i} entry without an EventId: {Repr(e)}");
                eventIds.Add(e.EventId);
            }
        }

        var dupeIds = eventIds.GroupBy(id => id, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Ensure(
            dupeIds.Count == 0,
            $"PutEvents minted a duplicate EventId across concurrent callers: {SortedRepr(dupeIds)} " +
            $"(all 40 were [{string.Join(", ", eventIds.Select(id => $"'{id}'"))}])");

        var delivered = new List<string>();
        var started = FidelityTime.StartNew();
        while (started.Elapsed < TimeSpan.FromSeconds(240.0) && delivered.Distinct(StringComparer.Ordinal).Count() < 40)
        {
            foreach (var msg in await Collect(ctx, queue.Url, windowS: 10.0, maxTotal: 100))
            {
                if (NonceOf(Body(msg)) is { } nonce && published.Contains(nonce))
                {
                    delivered.Add(nonce);
                }
            }
        }

        var deliveredSet = delivered.ToHashSet(StringComparer.Ordinal);
        var missing = published.Except(deliveredSet).ToList();
        Ensure(
            missing.Count == 0,
            $"rule {rule} lost {missing.Count} of 40 concurrently published events; missing nonces " +
            $"{SortedRepr(missing)} (delivered {deliveredSet.Count} distinct of {delivered.Count} messages)");
        // At-least-once is the documented contract, so a redelivery is reported, never failed.
        var duplicates = delivered.Count - deliveredSet.Count;
        Console.WriteLine($"[observed] 40/40 nonces delivered; at-least-once redeliveries observed: {duplicates}");

        var trailing = await Collect(ctx, queue.Url, windowS: 10.0, maxTotal: 100);
        var unknown = trailing
            .Select(Body)
            .Where(b => !(NonceOf(b) is { } n && published.Contains(n)))
            .ToList();
        Ensure(
            unknown.Count == 0,
            $"queue {queue.Name} received {unknown.Count} message(s) carrying no published nonce, so " +
            $"the set comparison above was measured against contaminated traffic: [{string.Join(", ", unknown.Select(b => b?.ToJsonString()))}]");
    }

    private static string SortedRepr(IEnumerable<string> values) =>
        $"[{string.Join(", ", values.Order(StringComparer.Ordinal).Select(v => $"'{v}'"))}]";

    private static string Repr(PutEventsResultEntry e) =>
        $"{{'EventId': '{e.EventId}', 'ErrorCode': '{e.ErrorCode}', 'ErrorMessage': '{e.ErrorMessage}'}}";

    private static string? NonceOf(JsonNode? body) =>
        body is JsonObject o && o["detail"] is JsonObject d && d["nonce"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private sealed record Queue(string Name, string Url, string Arn);

    private static Dictionary<string, string> XsvcResourceTags() =>
        new() { [ManagedTagKey] = "true", ["sim2real:prefix"] = XsvcPrefix.TrimEnd('-') };

    /// <summary>A unique, prefix-tagged resource name: sim2real-xsvc-&lt;kind&gt;-&lt;uuid&gt;.</summary>
    private static string XName(string kind) => FidelityContext.UniqueName($"{XsvcPrefix}{kind}-");

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
            foreach (var msg in await Collect(ctx, queueUrl, windowS: Math.Min(8.0, timeoutS), maxTotal: 100))
            {
                (predicate(Body(msg)) ? matched : others).Add(msg);
            }
        }

        return (matched, others);
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
    /// Create an EventBridge rule for `pattern`, grant the rule SendMessage on `queue`, and attach
    /// the queue as target t1. Registers remove_targets + delete_rule teardown (reverse order of
    /// creation). Returns the rule name.
    /// </summary>
    private static async Task<string> PutRuleWithSqsTarget(FidelityContext ctx, JsonObject pattern, Queue queue, string? bus = null, Target? target = null)
    {
        var events = ctx.Events;
        var name = XName("rule");
        var ruleArn = (await events.PutRuleAsync(new PutRuleRequest
        {
            Name = name,
            EventPattern = pattern.ToJsonString(),
            EventBusName = bus,
        })).RuleArn;
        ctx.OnCleanup(() => events.DeleteRuleAsync(new DeleteRuleRequest { Name = name, EventBusName = bus, Force = true }));
        await SetQueuePolicy(ctx, queue.Url, AllowServiceSend(queue.Arn, "events.amazonaws.com", ruleArn));
        var tgt = target ?? new Target();
        tgt.Id = "t1";
        tgt.Arn = queue.Arn;
        await events.PutTargetsAsync(new PutTargetsRequest { Rule = name, Targets = [tgt], EventBusName = bus });
        ctx.OnCleanup(() => events.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = name, Ids = ["t1"], Force = true, EventBusName = bus }));
        return name;
    }

    /// <summary>
    /// Emit single control events until `consecutive` of them round-trip back to back.
    /// </summary>
    /// <remarks>
    /// A rule and its target become effective progressively on real EventBridge, and a dropped event is
    /// never retried, so one arrival proves only that SOME path is live. Requiring several consecutive
    /// round trips is the barrier that makes a later shortfall attributable to loss rather than to
    /// warm-up. This polls an observable condition (the control's own arrival) rather than sleeping.
    /// </remarks>
    private static async Task SettleDeliveryPath(FidelityContext ctx, string source, string queueUrl, int consecutive = 3)
    {
        var events = ctx.Events;
        var streak = 0;
        for (var i = 0; i < 30; i++)
        {
            var nonce = XName("ctl");
            await events.PutEventsAsync(new PutEventsRequest
            {
                Entries = [new PutEventsRequestEntry { Source = source, DetailType = "Control", Detail = $$"""{"nonce": "{{nonce}}"}""" }],
            });
            var (got, _) = await WaitFor(ctx, queueUrl, b => b is JsonObject o && (string?)o["detail"]?["nonce"] == nonce, timeoutS: 15.0);
            streak = got.Count > 0 ? streak + 1 : 0;
            if (streak >= consecutive)
            {
                return;
            }
        }

        throw Fail(
            $"the rule -> {queueUrl} delivery path never round-tripped {consecutive} consecutive " +
            "control events, so nothing measured after this point would be attributable to loss " +
            $"rather than to warm-up (longest streak reached: {streak})");
    }
}

// Translated from behavioral/events__sqs/check_eventbridge_putevents_partial_batch_delivers_valid_entry_only.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.EventBridge.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.EventsSqs;

/// <summary>events__sqs.eventbridge-putevents-partial-batch-delivers-valid-entry-only</summary>
[Category("AwsEvaluationSuite")]
public sealed class EventbridgePuteventsPartialBatchDeliversValidEntryOnly
{
    private const string XsvcPrefix = "sim2real-xsvc-";
    private const string MalformedDetailMessage = "Detail is malformed.";
    private const string ManagedTagKey = "sim2real:managed";

    /// <summary>
    /// A PutEvents batch with one malformed Detail reports FailedEntryCount 1 with MalformedDetail
    /// and its exact message at that index, while the valid entry gets an EventId and is delivered.
    /// </summary>
    [Test]
    [DisplayName("events__sqs.eventbridge-putevents-partial-batch-delivers-valid-entry-only")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var sqs = ctx.Sqs;
        var queue = await CreateQueue(ctx);

        var busName = XName("bus");
        await events.CreateEventBusAsync(new CreateEventBusRequest { Name = busName });
        ctx.OnCleanup(() => ctx.Events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = busName }));

        var ruleName = XName("rule");
        var rule = await events.PutRuleAsync(new PutRuleRequest
        {
            Name = ruleName,
            EventBusName = busName,
            EventPattern = """{"source": ["sim2real.xsvc.batch"]}""",
            State = "ENABLED",
        });
        ctx.OnCleanup(() => ctx.Events.DeleteRuleAsync(new DeleteRuleRequest { Name = ruleName, EventBusName = busName }));
        // An SQS target needs a resource policy; without it EventBridge drops the event silently.
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = queue.Url,
            Attributes = new()
            {
                ["Policy"] = new JsonObject
                {
                    ["Version"] = "2012-10-17",
                    ["Statement"] = new JsonArray(new JsonObject
                    {
                        ["Effect"] = "Allow",
                        ["Principal"] = new JsonObject { ["Service"] = "events.amazonaws.com" },
                        ["Action"] = "sqs:SendMessage",
                        ["Resource"] = queue.Arn,
                        ["Condition"] = new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = rule.RuleArn } },
                    }),
                }.ToJsonString(),
            },
        });
        await events.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = ruleName,
            EventBusName = busName,
            Targets = [new Target { Id = "sqs-target", Arn = queue.Arn }],
        });
        ctx.OnCleanup(() => ctx.Events.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = ruleName, EventBusName = busName, Ids = ["sqs-target"] }));

        var goodMarker = XName("good");
        var badMarker = XName("bad");

        Task<PutEventsResponse> PutBatch() => events.PutEventsAsync(new PutEventsRequest
        {
            Entries =
            [
                new PutEventsRequestEntry
                {
                    Source = "sim2real.xsvc.batch",
                    DetailType = "BatchProbe",
                    Detail = $$"""{"marker": "{{goodMarker}}"}""",
                    EventBusName = busName,
                },
                new PutEventsRequestEntry
                {
                    Source = "sim2real.xsvc.batch",
                    DetailType = "BatchProbe",
                    // Detail must be a JSON OBJECT string; a bare scalar is malformed.
                    Detail = $"\"{badMarker}\"",
                    EventBusName = busName,
                },
            ],
        });

        var resp = await PutBatch();
        Ensure(
            resp.FailedEntryCount == 1,
            $"PutEvents must fail exactly the malformed entry, got FailedEntryCount={resp.FailedEntryCount}: {Repr(resp)}");
        var entries = resp.Entries ?? [];
        Ensure(entries.Count == 2, $"Entries must be parallel to the request (2 entries), got [{string.Join(", ", entries.Select(Repr))}]");
        // Index alignment is the whole contract: the producer retries by position.
        Ensure(
            !string.IsNullOrEmpty(entries[0].EventId) && entries[0].ErrorCode is null,
            $"the valid entry at index 0 must carry an EventId and no ErrorCode: {Repr(entries[0])}");
        Ensure(
            entries[1].ErrorCode == "MalformedDetail" && entries[1].ErrorMessage == MalformedDetailMessage,
            $"the malformed entry at index 1 must report ErrorCode MalformedDetail with ErrorMessage " +
            $"'{MalformedDetailMessage}', got {Repr(entries[1])}");
        Ensure(entries[1].EventId is null, $"a failed entry must NOT be assigned an EventId: {Repr(entries[1])}");

        // Positive half: the valid sibling really was published, not just accounted for. 180s, re-firing
        // each poll: rule/target wiring is not live the instant put_targets returns, and an event
        // published before it is live is dropped.
        async Task<List<string>> ProbeDelivery()
        {
            await PutBatch();
            return await DrainQueue(ctx, queue.Url, windowS: 6.0);
        }

        var bodies = await WaitForEffect(
            ProbeDelivery,
            bs => bs.Any(b => b.Contains(goodMarker, StringComparison.Ordinal)),
            timeoutS: 180,
            intervalS: 2.0,
            description: $"valid batch entry {goodMarker} delivered to the SQS target");
        var delivered = bodies.Where(b => b.Contains(goodMarker, StringComparison.Ordinal)).Select(b => JsonNode.Parse(b)!).ToList();
        Ensure(
            JsonNode.DeepEquals(delivered[0]["detail"], new JsonObject { ["marker"] = goodMarker }),
            $"delivered detail mangled: {delivered[0].ToJsonString()}");
        Ensure(
            (string?)delivered[0]["source"] == "sim2real.xsvc.batch",
            $"delivered source wrong: '{delivered[0]["source"]}'");
        // Effect-absent half: the rejected entry must never reach the target.
        Ensure(
            !bodies.Any(b => b.Contains(badMarker, StringComparison.Ordinal)),
            $"the malformed entry was rejected by PutEvents but still delivered: [{string.Join(", ", bodies.Select(b => $"'{b}'"))}]");
    }

    private sealed record Queue(string Name, string Url, string Arn);

    private static string Repr(PutEventsResultEntry e)
    {
        var parts = new List<string>();
        if (e.EventId is not null)
        {
            parts.Add($"'EventId': '{e.EventId}'");
        }

        if (e.ErrorCode is not null)
        {
            parts.Add($"'ErrorCode': '{e.ErrorCode}'");
        }

        if (e.ErrorMessage is not null)
        {
            parts.Add($"'ErrorMessage': '{e.ErrorMessage}'");
        }

        return $"{{{string.Join(", ", parts)}}}";
    }

    private static string Repr(PutEventsResponse r) =>
        $"{{'FailedEntryCount': {r.FailedEntryCount}, 'Entries': [{string.Join(", ", (r.Entries ?? []).Select(Repr))}]}}";

    private static Dictionary<string, string> XsvcResourceTags() =>
        new() { [ManagedTagKey] = "true", ["sim2real:prefix"] = XsvcPrefix.TrimEnd('-') };

    /// <summary>A unique, prefix-tagged resource name: sim2real-xsvc-&lt;kind&gt;-&lt;uuid&gt;.</summary>
    private static string XName(string kind) => FidelityContext.UniqueName($"{XsvcPrefix}{kind}-");

    /// <summary>Poll probe() until predicate(result) is truthy; return the final result (raises on timeout).</summary>
    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutS = 60.0, double intervalS = 1.0, string description = "condition")
    {
        var started = FidelityTime.StartNew();
        var last = default(T);
        while (started.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"{description} not met within {timeoutS}s. Last result: {(last is List<string> l ? $"[{string.Join(", ", l.Select(b => $"'{b}'"))}]" : $"{last}")}");
    }

    /// <summary>
    /// Poll for an async cross-service effect (default 120s — Kinesis/DDB-stream ESMs can need
    /// 60-120s for cold-start + first delivery; fast effects return as soon as the predicate holds).
    /// </summary>
    private static Task<T> WaitForEffect<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutS = 120.0, double intervalS = 2.0, string description = "effect") =>
        PollUntil(probe, predicate, timeoutS: timeoutS, intervalS: intervalS, description: description);

    /// <summary>
    /// Receive-and-delete everything visible on a queue for a bounded window; return raw bodies.
    /// </summary>
    /// <remarks>
    /// Used for the effect-ABSENT half of a delivery assertion: an empty return over a bounded
    /// window is the observable "nothing was delivered", and draining first gives the positive
    /// half a queue whose contents are attributable to exactly one publish.
    /// </remarks>
    private static async Task<List<string>> DrainQueue(FidelityContext ctx, string queueUrl, double windowS = 12.0)
    {
        var sqs = ctx.Sqs;
        var bodies = new List<string>();
        var started = FidelityTime.StartNew();
        while (started.Elapsed < TimeSpan.FromSeconds(windowS))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 });
            foreach (var msg in resp.Messages ?? [])
            {
                bodies.Add(msg.Body);
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
            }
        }

        return bodies;
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
}

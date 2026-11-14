// Translated from behavioral/events__sqs/check_put_targets_same_id_upsert_moves_delivery_to_the_new_queue.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.EventBridge.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using SqsMessage = Amazon.SQS.Model.Message;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.EventsSqs;

/// <summary>events__sqs.put-targets-same-id-upsert-moves-delivery-to-the-new-queue</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutTargetsSameIdUpsertMovesDeliveryToTheNewQueue
{
    private const string XsvcPrefix = "sim2real-xsvc-";
    private const string ManagedTagKey = "sim2real:managed";

    /// <summary>
    /// A second PutTargets with an existing target Id replaces that target; it does not add one.
    /// Re-sending t1 with queue B and InputPath $.detail leaves exactly one target; the bare detail
    /// then reaches B while queue A receives nothing for that event and never a bare detail.
    /// </summary>
    [Test]
    [DisplayName("events__sqs.put-targets-same-id-upsert-moves-delivery-to-the-new-queue")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var source = XName("src");
        var queueA = await CreateQueue(ctx);
        var queueB = await CreateQueue(ctx);
        var rule = await PutRuleWithSqsTarget(ctx, new JsonObject { ["source"] = new JsonArray(source) }, queueA);
        var ruleArn = (await events.DescribeRuleAsync(new DescribeRuleRequest { Name = rule })).Arn;
        await SetQueuePolicy(ctx, queueB.Url, AllowServiceSend(queueB.Arn, "events.amazonaws.com", ruleArn));

        PutEventsRequestEntry Entry(string marker) =>
            new() { Source = source, DetailType = "Sim2realUpsert", Detail = $$"""{"marker": "{{marker}}"}""" };

        var (first, _) = await EmitUntil(
            ctx,
            [Entry("to-a")],
            queueA.Url,
            b => b is JsonObject && DetailString(b, "marker") == "to-a");
        Ensure(first.Count > 0, "the original target never delivered its control event to queue A");

        var resp = await events.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = rule,
            Targets = [new Target { Id = "t1", Arn = queueB.Arn, InputPath = "$.detail" }],
        });
        Ensure(
            (resp.FailedEntryCount ?? 0) == 0,
            $"the upsert of t1 was refused: {{'FailedEntryCount': {resp.FailedEntryCount}, 'FailedEntries': " +
            $"[{string.Join(", ", (resp.FailedEntries ?? []).Select(f => $"{{'TargetId': '{f.TargetId}', 'ErrorCode': '{f.ErrorCode}', 'ErrorMessage': '{f.ErrorMessage}'}}"))}]}}");

        var targets = (await events.ListTargetsByRuleAsync(new ListTargetsByRuleRequest { Rule = rule })).Targets ?? [];
        Ensure(targets.Count == 1, $"after the upsert the rule lists {targets.Count} targets, expected 1");
        Ensure(
            targets[0].Id == "t1" && targets[0].Arn == queueB.Arn,
            $"after the upsert the target is Id '{targets[0].Id}' on queue " +
            $"{(targets[0].Arn == queueB.Arn ? "B" : targets[0].Arn == queueA.Arn ? "A" : "unknown")}");
        Ensure(
            targets[0].InputPath == "$.detail",
            $"after the upsert t1 has InputPath {(targets[0].InputPath is null ? "None" : $"'{targets[0].InputPath}'")}, expected '$.detail'");

        int? converged = null;
        var seenOnA = new List<JsonNode?>();
        var history = new List<string>();
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var marker = $"to-b-{attempt}";
            await events.PutEventsAsync(new PutEventsRequest { Entries = [Entry(marker)] });
            var (got, _) = await WaitFor(ctx, queueB.Url, b => JsonNode.DeepEquals(b, new JsonObject { ["marker"] = marker }), timeoutS: 10.0);
            var onA = (await Collect(ctx, queueA.Url, windowS: 5.0)).Select(Body).ToList();
            seenOnA.AddRange(onA);
            var aMarkers = onA.Select(b => b is JsonObject ? JsonValue.Create(DetailString(b, "marker")) : b).ToList();
            history.Add($"({attempt}, {(got.Count > 0 ? "True" : "False")}, [{string.Join(", ", aMarkers.Select(PyRepr))}])");
            if (got.Count > 0 && !aMarkers.Any(m => m is JsonValue v && v.TryGetValue<string>(out var s) && s == marker))
            {
                converged = attempt;
                break;
            }
        }

        Ensure(
            converged is not null,
            "delivery never converged to the upserted target (event on queue B as bare detail, nothing " +
            $"on queue A); per round (attempt, reached B, markers on A): [{string.Join(", ", history)}]");
        var bareOnA = seenOnA.Where(b => b is JsonObject o && !o.ContainsKey("detail")).ToList();
        Ensure(bareOnA.Count == 0, $"queue A received bare-detail bodies, the NEW target config: [{string.Join(", ", bareOnA.Select(PyRepr))}]");
    }

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

    /// <summary>body.get("detail", {}).get(key) for a parsed body that is a dict.</summary>
    private static string? DetailString(JsonNode? body, string key) =>
        body is JsonObject o && o["detail"] is JsonObject d && d[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private sealed record Queue(string Name, string Url, string Arn);

    private static Dictionary<string, string> XsvcResourceTags() =>
        new() { [ManagedTagKey] = "true", ["sim2real:prefix"] = XsvcPrefix.TrimEnd('-') };

    /// <summary>A unique, prefix-tagged resource name: sim2real-xsvc-&lt;kind&gt;-&lt;uuid&gt;.</summary>
    private static string XName(string kind) => FidelityContext.UniqueName($"{XsvcPrefix}{kind}-");

    private static List<Amazon.EventBridge.Model.Tag> XsvcTagsKv() =>
        [.. XsvcResourceTags().Select(kv => new Amazon.EventBridge.Model.Tag { Key = kv.Key, Value = kv.Value })];

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
    /// <remarks>
    /// This is the NEGATIVE observable. An empty return is only meaningful once a positive control
    /// has already been observed elsewhere in the check -- that is what proves enough wall-clock and
    /// enough propagation elapsed for a delivery to have happened if the emulator were going to make
    /// one. Used alone it would pass against an emulator that delivers nothing at all.
    /// </remarks>
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
    /// Poll a queue until a message whose parsed body satisfies predicate() arrives.
    /// Returns (matched, others) -- every non-matching message seen along the way is returned too so
    /// the caller can assert on leakage instead of silently discarding it.
    /// </summary>
    private static async Task<(List<SqsMessage> Matched, List<SqsMessage> Others)> WaitFor(
        FidelityContext ctx, string queueUrl, Func<JsonNode?, bool> predicate, double timeoutS = 60.0)
    {
        var matched = new List<SqsMessage>();
        var others = new List<SqsMessage>();
        var started = FidelityTime.StartNew();
        while (started.Elapsed < TimeSpan.FromSeconds(timeoutS) && matched.Count == 0)
        {
            foreach (var msg in await Collect(ctx, queueUrl, windowS: Math.Min(8.0, timeoutS)))
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
        ctx.OnCleanup(() => events.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = name, Ids = ["t1"], EventBusName = bus }));
        return name;
    }

    /// <summary>
    /// PutEvents ALL of `entries` together, repeatedly, until a message whose parsed body satisfies
    /// `predicate` lands on `queue_url`. Returns (matched, others).
    /// </summary>
    /// <remarks>
    /// Emitting every leg on every attempt is what makes the negative sound: a freshly created
    /// rule/target silently drops events until the path goes live and EventBridge never retries a
    /// dropped event, so the control has to be re-emitted -- and if the non-matching legs ride along
    /// on each attempt then, by the time the control arrives, every non-matching event has had at
    /// least as long to arrive as the control did. Non-delivery is therefore suppression, not lag.
    /// </remarks>
    private static async Task<(List<SqsMessage> Matched, List<SqsMessage> Others)> EmitUntil(
        FidelityContext ctx, List<PutEventsRequestEntry> entries, string queueUrl, Func<JsonNode?, bool> predicate,
        int attempts = 10, double pollS = 8.0)
    {
        var events = ctx.Events;
        var others = new List<SqsMessage>();
        for (var i = 0; i < attempts; i++)
        {
            await events.PutEventsAsync(new PutEventsRequest { Entries = [.. entries] });
            var (got, extra) = await WaitFor(ctx, queueUrl, predicate, timeoutS: pollS);
            others.AddRange(extra);
            if (got.Count > 0)
            {
                return (got, others);
            }
        }

        throw Fail(
            $"control event never delivered to {queueUrl} after {attempts} PutEvents attempts " +
            $"(non-matching messages seen meanwhile: {others.Count}) -- the positive leg of this " +
            "negative-delivery check could not be established, so the negative is unprovable");
    }
}

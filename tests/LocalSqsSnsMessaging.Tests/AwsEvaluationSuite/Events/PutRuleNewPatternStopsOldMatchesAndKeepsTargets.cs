// Translated from behavioral/events/check_put_rule_new_pattern_stops_old_matches_and_keeps_targets.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.put-rule-new-pattern-stops-old-matches-and-keeps-targets</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutRuleNewPatternStopsOldMatchesAndKeepsTargets
{
    private const string Check = "put-rule-new-pattern-stops-old-matches-and-keeps-targets";
    private const string DetailType = "sim2real-pattern-swap";
    private const string TargetId = "sim2real-target";

    /// <summary>
    /// A second PutRule with a new EventPattern keeps the rule's ARN and its target, reads back the new
    /// pattern, delivers an event matching it, and never delivers an event matching only the old pattern.
    /// </summary>
    [Test]
    [DisplayName("events.put-rule-new-pattern-stops-old-matches-and-keeps-targets")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var rule = FidelityContext.UniqueName("sim2real-evb-swap-");
        var srcA = FidelityContext.UniqueName("sim2real.swap.a.");
        var srcB = FidelityContext.UniqueName("sim2real.swap.b.");
        var patternB = new JsonObject { ["source"] = new JsonArray(srcB), ["detail-type"] = new JsonArray(DetailType) };

        var ruleArn = (await events.PutRuleAsync(new PutRuleRequest
        {
            Name = rule,
            EventPattern = $$"""{"source": ["{{srcA}}"], "detail-type": ["{{DetailType}}"]}""",
            State = RuleState.ENABLED,
            Tags = [new Amazon.EventBridge.Model.Tag { Key = "sim2real", Value = Check }],
        })).RuleArn;
        ctx.OnCleanup(() => events.DeleteRuleAsync(new DeleteRuleRequest { Name = rule }));
        var (queueUrl, queueArn) = await CreateQueueForRule(ctx, ruleArn, "sim2real-evb-swap-q-");
        var resp = await events.PutTargetsAsync(new PutTargetsRequest { Rule = rule, Targets = [new Target { Id = TargetId, Arn = queueArn }] });
        Ensure(resp.FailedEntryCount == 0, $"PutTargets failed: {resp.FailedEntryCount}");
        ctx.OnCleanup(() => events.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = rule, Ids = [TargetId] }));

        await Converge(ctx, srcA, queueUrl, "pattern-a");

        var again = await events.PutRuleAsync(new PutRuleRequest { Name = rule, EventPattern = patternB.ToJsonString(), State = RuleState.ENABLED });
        Ensure(again.RuleArn == ruleArn, "the second PutRule on the same name answered a different RuleArn");
        var described = await events.DescribeRuleAsync(new DescribeRuleRequest { Name = rule });
        Ensure(
            JsonNode.DeepEquals(JsonNode.Parse(described.EventPattern ?? throw new KeyNotFoundException("'EventPattern'")), patternB),
            $"DescribeRule after the update reads pattern '{described.EventPattern}', expected the B pattern");
        var targets = (await events.ListTargetsByRuleAsync(new ListTargetsByRuleRequest { Rule = rule })).Targets
            ?? throw new KeyNotFoundException("'Targets'");
        Ensure(
            targets.Select(t => (t.Id, t.Arn)).SequenceEqual([(TargetId, queueArn)]),
            $"after PutRule replaced the pattern the rule lists {targets.Count} target(s) " +
            $"[{string.Join(", ", targets.Select(t => $"'{t.Id}'"))}]; expected exactly the original one");

        await Converge(ctx, srcB, queueUrl, "pattern-b");

        var old = $"old-pattern-{Guid.NewGuid():N}";
        var control = $"new-pattern-{Guid.NewGuid():N}";
        var put = await events.PutEventsAsync(new PutEventsRequest
        {
            Entries =
            [
                new PutEventsRequestEntry { Source = srcA, DetailType = DetailType, Detail = $$"""{"marker": "{{old}}"}""" },
                new PutEventsRequestEntry { Source = srcB, DetailType = DetailType, Detail = $$"""{"marker": "{{control}}"}""" },
            ],
        });
        Ensure(put.FailedEntryCount == 0, $"PutEvents refused an entry: {string.Join(", ", (put.Entries ?? []).Select(e => $"({e.EventId}, {e.ErrorCode}, {e.ErrorMessage})"))}");
        var started = FidelityTime.StartNew();
        var (body, seen) = await AwaitMarker(ctx, queueUrl, control, 120.0);
        Ensure(body is not null, "the event matching the new pattern (positive control) was never delivered");
        Ensure(
            (body["source"] ?? throw new KeyNotFoundException("'source'")) is JsonValue v && v.TryGetValue<string>(out var s) && s == srcB,
            "the delivered control event carries a different source than it was put with");
        foreach (var (k, b) in await Drain(ctx, queueUrl, Math.Max(15.0, 3 * started.Elapsed.TotalSeconds)))
        {
            seen[k] = b;
        }

        Ensure(!seen.ContainsKey(old), "an event matching only the replaced pattern was still delivered after the update");
    }

    /// <summary>An SQS queue whose policy lets EventBridge deliver from <c>rule_arn</c> only.</summary>
    private static async Task<(string QueueUrl, string QueueArn)> CreateQueueForRule(FidelityContext ctx, string ruleArn, string prefix)
    {
        var sqs = ctx.Sqs;
        var queueUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName(prefix), Tags = new() { ["sim2real"] = Check } })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(queueUrl));
        var queueArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = queueUrl, AttributeNames = ["QueueArn"] }))
            .Attributes["QueueArn"];
        var policy = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Sid"] = "AllowEventBridgeDelivery",
                ["Effect"] = "Allow",
                ["Principal"] = new JsonObject { ["Service"] = "events.amazonaws.com" },
                ["Action"] = "sqs:SendMessage",
                ["Resource"] = queueArn,
                ["Condition"] = new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = ruleArn } },
            }),
        };
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = queueUrl, Attributes = new() { ["Policy"] = policy.ToJsonString() } });
        return (queueUrl, queueArn);
    }

    private static async Task Put(FidelityContext ctx, string source, string marker)
    {
        var resp = await ctx.Events.PutEventsAsync(new PutEventsRequest
        {
            Entries = [new PutEventsRequestEntry { Source = source, DetailType = DetailType, Detail = $$"""{"marker": "{{marker}}"}""" }],
        });
        Ensure(resp.FailedEntryCount == 0, $"PutEvents refused an entry: {string.Join(", ", (resp.Entries ?? []).Select(e => $"({e.EventId}, {e.ErrorCode}, {e.ErrorMessage})"))}");
    }

    /// <summary>The marker of a delivered message: the envelope's detail.marker, or a transformed body's.</summary>
    private static JsonNode? MarkerOf(JsonObject body)
    {
        if (body["detail"] is JsonObject detail && detail.ContainsKey("marker"))
        {
            return detail["marker"];
        }

        return body["marker"];
    }

    /// <summary>Python truthiness of a parsed JSON value used as a marker.</summary>
    private static string? Truthy(JsonNode? marker) =>
        marker is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    /// <summary>Receive and delete everything on <c>queue_url</c> for <c>window_s</c> seconds; marker -&gt; body.</summary>
    private static async Task<Dictionary<string, JsonObject>> Drain(FidelityContext ctx, string queueUrl, double windowS)
    {
        var sqs = ctx.Sqs;
        var seen = new Dictionary<string, JsonObject>();
        var started = FidelityTime.StartNew();
        while (started.Elapsed < TimeSpan.FromSeconds(windowS))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                var body = JsonNode.Parse(msg.Body)!.AsObject();
                if (Truthy(MarkerOf(body)) is { } marker)
                {
                    seen[marker] = body;
                }
            }
        }

        return seen;
    }

    /// <summary>Drain <c>queue_url</c> until <c>marker</c> arrives or <c>timeout_s</c> passes; (its body or None, all seen).</summary>
    private static async Task<(JsonObject? Body, Dictionary<string, JsonObject> Seen)> AwaitMarker(FidelityContext ctx, string queueUrl, string marker, double timeoutS)
    {
        var seen = new Dictionary<string, JsonObject>();
        var started = FidelityTime.StartNew();
        while (started.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            foreach (var (k, b) in await Drain(ctx, queueUrl, 4.0))
            {
                seen[k] = b;
            }

            if (seen.TryGetValue(marker, out var body))
            {
                return (body, seen);
            }
        }

        return (null, seen);
    }

    /// <summary>
    /// Send probes from <c>source</c> until <c>streak</c> in a row reach <c>queue_url</c>: a rule or target
    /// update is eventually consistent, so the claim is graded only once the update is stably live.
    /// </summary>
    private static async Task<Dictionary<string, JsonObject>> Converge(FidelityContext ctx, string source, string queueUrl, string label, int attempts = 20, int streak = 3)
    {
        var seen = new Dictionary<string, JsonObject>();
        var runOf = 0;
        for (var i = 0; i < attempts; i++)
        {
            var probe = $"{label}-probe-{i}-{Guid.NewGuid():N}";
            await Put(ctx, source, probe);
            var (body, got) = await AwaitMarker(ctx, queueUrl, probe, 15.0);
            foreach (var (k, b) in got)
            {
                seen[k] = b;
            }

            runOf = body is not null ? runOf + 1 : 0;
            if (runOf >= streak)
            {
                return seen;
            }
        }

        throw Fail($"{label} probes did not reach their queue {streak} times in a row within {attempts} attempts");
    }
}

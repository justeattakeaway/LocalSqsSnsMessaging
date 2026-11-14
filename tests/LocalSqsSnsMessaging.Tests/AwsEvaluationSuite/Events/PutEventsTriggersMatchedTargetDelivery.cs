// Translated from behavioral/events/check_put_events_triggers_matched_target_delivery.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.put-events-triggers-matched-target-delivery</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutEventsTriggersMatchedTargetDelivery
{
    private const string Check = "put-events-triggers-matched-target-delivery";

    // The fixed bounds of a "nothing arrives" window.
    private const double AbsenceFloorS = 10.0;
    private const double AbsenceCapS = 60.0;

    /// <summary>
    /// A matching PutEvents entry reaches the rule's SQS target with its envelope; a non-matching
    /// one does not, and once the rule is deleted a matching one does not either. A source-only twin
    /// rule's queue receiving each event proves the bus dispatched it before absence is graded.
    /// </summary>
    [Test]
    [DisplayName("events.put-events-triggers-matched-target-delivery")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EventBridgeErrorMessages, "DescribeRule of a deleted rule says '... does not exist on event bus default.' instead of '... on EventBus default.'")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;

        var ruleName = FidelityContext.UniqueName("sim2real-evb-rule-");
        var controlName = FidelityContext.UniqueName("sim2real-evb-ctl-");
        var source = FidelityContext.UniqueName("sim2real.matched.");
        const string detailType = "sim2real-matched-type";

        var ruleArn = await PutRule(ctx, events, ruleName, $$"""{"source": ["{{source}}"], "detail-type": ["{{detailType}}"]}""");
        Ensure(
            (await ListRules(events, ruleName)).Select(r => r.Name).SequenceEqual([ruleName]),
            $"live rule {ruleName} is not listed by ListRules");
        var controlArn = await PutRule(ctx, events, controlName, $$"""{"source": ["{{source}}"]}""");

        var (queueUrl, queueArn) = await CreateQueueWithEventbridgePolicy(ctx, ruleArn);
        var (controlUrl, controlQueueArn) = await CreateQueueWithEventbridgePolicy(ctx, controlArn);
        await PutTarget(ctx, events, ruleName, queueArn);
        await PutTarget(ctx, events, controlName, controlQueueArn);

        // A non-matching event (same source, other detail-type) goes first with its own marker.
        var otherMarker = FidelityContext.UniqueName("other-marker-");
        var otherResp = await events.PutEventsAsync(new PutEventsRequest
        {
            Entries = [new PutEventsRequestEntry { Source = source, DetailType = "sim2real-unmatched-type", Detail = $$"""{"marker": "{{otherMarker}}"}""" }],
        });
        Ensure(otherResp.FailedEntryCount == 0, $"put_events rejected the entry: {otherResp.FailedEntryCount}");

        var marker = FidelityContext.UniqueName("marker-");
        var putResp = await events.PutEventsAsync(new PutEventsRequest
        {
            Entries = [new PutEventsRequestEntry { Source = source, DetailType = detailType, Detail = $$"""{"marker": "{{marker}}"}""" }],
        });
        Ensure(putResp.FailedEntryCount == 0, $"put_events rejected the entry: {putResp.FailedEntryCount}");
        var eventId = (putResp.Entries ?? throw new KeyNotFoundException("'Entries'"))[0].EventId ?? throw new KeyNotFoundException("'EventId'");

        var envelope = await PollForMarker(ctx, queueUrl, marker, timeoutS: 60.0, forbidden: otherMarker);
        Ensure(Str(envelope, "source") == source, $"expected source '{source}', got '{Str(envelope, "source")}'");
        Ensure(
            Str(envelope, "detail-type") == detailType,
            $"expected detail-type '{detailType}', got '{Str(envelope, "detail-type")}'");
        Ensure(
            (string?)envelope["detail"]!["marker"] == marker,
            $"expected marker '{marker}', got '{envelope["detail"]!["marker"]}'");
        Ensure(
            Str(envelope, "id") == eventId,
            $"delivered envelope id '{Str(envelope, "id")}' does not match PutEvents EventId '{eventId}'");
        Ensure(envelope.ContainsKey("time"), $"delivered envelope has no time field: [{string.Join(", ", envelope.Select(kv => kv.Key).Order(StringComparer.Ordinal).Select(k => $"'{k}'"))}]");
        Ensure(
            envelope["region"] is JsonValue r && r.TryGetValue<string>(out var region) && region == ctx.Region,
            $"delivered envelope region '{envelope["region"]}', expected '{ctx.Region}'");
        Ensure(
            envelope["account"] is JsonValue a && a.TryGetValue<string>(out var account) && account == ctx.Account,
            $"delivered envelope account '{envelope["account"]}', expected '{ctx.Account}'");

        // Twin control: the source-only rule delivers BOTH markers, so the bus has dispatched the
        // non-matching event; only then is the primary queue drained and must not hold it.
        await CollectMarkers(ctx, controlUrl, [marker, otherMarker], timeoutS: 60.0);
        var leftover = await Drain(ctx, queueUrl);
        Ensure(!leftover.Contains(otherMarker), $"the non-matching event '{otherMarker}' was delivered to the rule's target");
        Ensure(leftover.Count == 0, $"unexpected extra deliveries to the rule's target: {Repr(leftover)}");

        // The primary rule is deleted and proven gone; a matching event then reaches only the twin.
        await events.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = ruleName, Ids = ["sim2real-target"] });
        await events.DeleteRuleAsync(new DeleteRuleRequest { Name = ruleName });
        await AssertRuleGone(events, ruleName, queueArn);

        var afterMarker = FidelityContext.UniqueName("after-delete-marker-");
        var sent = FidelityTime.StartNew();
        var afterResp = await events.PutEventsAsync(new PutEventsRequest
        {
            Entries = [new PutEventsRequestEntry { Source = source, DetailType = detailType, Detail = $$"""{"marker": "{{afterMarker}}"}""" }],
        });
        Ensure(afterResp.FailedEntryCount == 0, $"put_events rejected the entry: {afterResp.FailedEntryCount}");
        await CollectMarkers(ctx, controlUrl, [afterMarker], timeoutS: 60.0);
        // Absence is graded for at least as long as the twin's delivery took (floor 10 s, cap 60 s).
        var window = Math.Min(Math.Max(sent.Elapsed.TotalSeconds, AbsenceFloorS), AbsenceCapS);
        var late = await Listen(ctx, queueUrl, window);
        Ensure(
            !late.Contains(afterMarker),
            $"an event matching the deleted rule {ruleName} was delivered to its former target");
        Ensure(late.Count == 0, $"unexpected deliveries to the deleted rule's target: {Repr(late.OfType<string>())}");

        await events.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = controlName, Ids = ["sim2real-target"] });
        await events.DeleteRuleAsync(new DeleteRuleRequest { Name = controlName });
        await AssertRuleGone(events, controlName, controlQueueArn);
    }

    private static string? Str(JsonObject body, string key) =>
        (body[key] ?? throw new KeyNotFoundException($"'{key}'")) is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static string Repr(IEnumerable<string?> items) =>
        $"[{string.Join(", ", items.Select(i => i is null ? "None" : $"'{i}'").Order(StringComparer.Ordinal))}]";

    /// <summary>Receive on the queue for the whole window; return the markers of every message seen.</summary>
    private static async Task<List<string?>> Listen(FidelityContext ctx, string queueUrl, double windowS)
    {
        var sqs = ctx.Sqs;
        var markers = new List<string?>();
        var started = FidelityTime.StartNew();
        double remaining;
        while ((remaining = windowS - started.Elapsed.TotalSeconds) > 0)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = Math.Max(1, Math.Min(5, (int)remaining)),
            });
            foreach (var msg in resp.Messages ?? [])
            {
                markers.Add(MarkerOf(JsonNode.Parse(msg.Body)));
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
            }
        }

        return markers;
    }

    private static async Task<string> PutRule(FidelityContext ctx, AmazonEventBridgeClient events, string name, string pattern)
    {
        var arn = (await events.PutRuleAsync(new PutRuleRequest
        {
            Name = name,
            EventPattern = pattern,
            State = RuleState.ENABLED,
            Tags = [new Amazon.EventBridge.Model.Tag { Key = "sim2real", Value = Check }],
        })).RuleArn;
        ctx.OnCleanup(() => IgnoreNotFound(() => events.DeleteRuleAsync(new DeleteRuleRequest { Name = name })));
        return arn;
    }

    private static async Task PutTarget(FidelityContext ctx, AmazonEventBridgeClient events, string rule, string targetArn)
    {
        var resp = await events.PutTargetsAsync(new PutTargetsRequest { Rule = rule, Targets = [new Target { Id = "sim2real-target", Arn = targetArn }] });
        Ensure(resp.FailedEntryCount == 0, $"put_targets failed: {resp.FailedEntryCount}");
        ctx.OnCleanup(() => IgnoreNotFound(() => events.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = rule, Ids = ["sim2real-target"] })));
        var targets = (await events.ListTargetsByRuleAsync(new ListTargetsByRuleRequest { Rule = rule })).Targets
            ?? throw new KeyNotFoundException("'Targets'");
        Ensure(targets.Any(t => t.Arn == targetArn), $"target was not registered on rule {rule}");
    }

    private static async Task AssertRuleGone(AmazonEventBridgeClient events, string name, string targetArn)
    {
        var gone = $"Rule {name} does not exist on EventBus default.";
        var described = await PollError(() => events.DescribeRuleAsync(new DescribeRuleRequest { Name = name }), "ResourceNotFoundException");
        AssertNotFound(described, $"DescribeRule on the deleted rule {name}", gone);
        await PollUntil(
            async () => (await ListRules(events, name)).Select(r => r.Name).ToList(),
            names => names.Count == 0,
            timeoutS: 60.0,
            description: $"deleted rule {name} leaving ListRules");
        var putDead = await PollError(
            () => events.PutTargetsAsync(new PutTargetsRequest { Rule = name, Targets = [new Target { Id = "sim2real-target", Arn = targetArn }] }),
            "ResourceNotFoundException");
        AssertNotFound(putDead, $"PutTargets on the deleted rule {name}", gone);
    }

    private static string? MarkerOf(JsonNode? body) =>
        body!.AsObject()["detail"] is JsonObject detail && detail["marker"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>Drain until every marker in `wanted` has arrived on the queue.</summary>
    private static async Task CollectMarkers(FidelityContext ctx, string queueUrl, HashSet<string> wanted, double timeoutS)
    {
        var sqs = ctx.Sqs;
        var seen = new HashSet<string?>();
        var started = FidelityTime.StartNew();
        while (started.Elapsed < TimeSpan.FromSeconds(timeoutS) && !seen.IsSupersetOf(wanted))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
            foreach (var msg in resp.Messages ?? [])
            {
                seen.Add(MarkerOf(JsonNode.Parse(msg.Body)));
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
            }
        }

        Ensure(
            seen.IsSupersetOf(wanted),
            $"control queue received {Repr(seen.OfType<string>())} within {timeoutS}s, " +
            $"expected {Repr(wanted)}");
    }

    /// <summary>Receive until one empty long poll; return the markers of every message drained.</summary>
    private static async Task<List<string?>> Drain(FidelityContext ctx, string queueUrl)
    {
        var sqs = ctx.Sqs;
        var markers = new List<string?>();
        while (true)
        {
            var msgs = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 })).Messages ?? [];
            if (msgs.Count == 0)
            {
                return markers;
            }

            foreach (var msg in msgs)
            {
                markers.Add(MarkerOf(JsonNode.Parse(msg.Body)));
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
            }
        }
    }

    private static async Task<(string QueueUrl, string QueueArn)> CreateQueueWithEventbridgePolicy(FidelityContext ctx, string ruleArn)
    {
        var sqs = ctx.Sqs;
        var queueName = FidelityContext.UniqueName("sim2real-evb-queue-");
        var queueUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = queueName, Tags = new() { ["sim2real"] = Check } })).QueueUrl;
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

    /// <summary>
    /// Drain until the envelope carrying `marker` arrives; an envelope carrying `forbidden`
    /// on the way is a failure.
    /// </summary>
    private static async Task<JsonObject> PollForMarker(FidelityContext ctx, string queueUrl, string marker, double timeoutS = 60.0, string forbidden = "")
    {
        var sqs = ctx.Sqs;
        var started = FidelityTime.StartNew();
        while (started.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
            foreach (var msg in resp.Messages ?? [])
            {
                var body = JsonNode.Parse(msg.Body)!.AsObject();
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                var got = MarkerOf(body);
                Ensure(
                    string.IsNullOrEmpty(forbidden) || got != forbidden,
                    $"the non-matching event '{forbidden}' was delivered to the rule's target: {body.ToJsonString()}");
                if (got == marker)
                {
                    return body;
                }
            }
        }

        throw Fail($"event with marker {marker} was never delivered to {queueUrl} within {timeoutS}s");
    }

    private static async Task IgnoreNotFound(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is not null && error.Code != "ResourceNotFoundException")
        {
            throw error.Exception;
        }
    }

    /// <summary>Poll fn until it fails with `code`; return the error response. Raise on timeout.</summary>
    private static async Task<AwsError> PollError(Func<Task> fn, string code, double timeoutS = 60.0, double intervalS = 2.0)
    {
        var started = FidelityTime.StartNew();
        var last = "<call succeeded>";
        while (started.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var error = await FidelityContext.CaptureError(fn);
            if (error is null)
            {
                last = "<call succeeded>";
            }
            else
            {
                last = error.Code ?? "";
                if (last == code)
                {
                    return error;
                }
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"expected {code}, got '{last}' after {timeoutS}s");
    }

    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutS, string description, double intervalS = 2.0)
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

        throw Fail($"{description} not observed within {timeoutS}s; last saw {(last is List<string> l ? Repr(l) : last?.ToString() ?? "None")}");
    }

    /// <summary>The response is ResourceNotFoundException with exactly <c>message</c> and an HTTP 4xx.</summary>
    private static void AssertNotFound(AwsError response, string what, string message)
    {
        var status = (int)response.StatusCode;
        Ensure(status >= 400 && status < 500, $"{what} carried HTTP {status}, expected 4xx");
        var got = (response.Code, response.Message);
        Ensure(
            got == ("ResourceNotFoundException", message),
            $"{what}: expected ('ResourceNotFoundException', '{message}'), got {got}");
    }

    private static async Task<List<Rule>> ListRules(AmazonEventBridgeClient events, string prefix)
    {
        var rules = new List<Rule>();
        string? token = null;
        do
        {
            var page = await events.ListRulesAsync(new ListRulesRequest { NamePrefix = prefix, NextToken = token });
            rules.AddRange(page.Rules ?? []);
            token = page.NextToken;
        }
        while (!string.IsNullOrEmpty(token));

        return rules;
    }
}

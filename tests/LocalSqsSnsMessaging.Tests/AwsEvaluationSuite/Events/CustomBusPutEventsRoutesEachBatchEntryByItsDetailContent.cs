// Translated from behavioral/events/check_custom_bus_put_events_routes_each_batch_entry_by_its_detail_content.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.custom-bus-put-events-routes-each-batch-entry-by-its-detail-content</summary>
[Category("AwsEvaluationSuite")]
public sealed class CustomBusPutEventsRoutesEachBatchEntryByItsDetailContent
{
    private const string Check = "custom-bus-put-events-routes-each-batch-entry-by-its-detail-content";
    private const string DetailType = "sim2real.order-status";

    /// <summary>A body whose detail has no marker (Python's <c>None</c> key).</summary>
    private const string NoMarker = "\0None";

    // (marker suffix, detail) per entry of the one PutEvents call; the rule matches level error/fatal
    // together with a nested region prefix.
    private static readonly (string Suffix, string Level, string Region)[] Entries =
    [
        ("err-eu", "error", "eu-frankfurt"),
        ("info-eu", "info", "eu-dublin"),
        ("fatal-eu", "fatal", "eu-paris"),
        ("err-us", "error", "us-virginia"),
    ];

    private static readonly string[] Matching = ["err-eu", "fatal-eu"];

    private const string PatternDetail = """{"level": ["error", "fatal"], "where": {"region": [{"prefix": "eu-"}]}}""";

    /// <summary>
    /// On a custom bus, one four-entry PutEvents is routed entry by entry: the rule matching level
    /// and a nested region prefix delivers exactly its two entries with their own EventIds, while a
    /// source-only twin rule receives all four first; after the rule is deleted it is NotFound on that bus.
    /// </summary>
    [Test]
    [DisplayName("events.custom-bus-put-events-routes-each-batch-entry-by-its-detail-content")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var bus = FidelityContext.UniqueName("sim2real-evb-bus-");
        var busArn = (await events.CreateEventBusAsync(new CreateEventBusRequest { Name = bus, Tags = [new Tag { Key = "sim2real", Value = Check }] })).EventBusArn;
        ctx.OnCleanup(() => IgnoreNotFound(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus })));
        Ensure(
            busArn.EndsWith($":event-bus/{bus}", StringComparison.Ordinal),
            $"CreateEventBus returned an ARN for another bus: '{busArn}'");

        var ruleName = FidelityContext.UniqueName("sim2real-evb-rule-");
        var controlName = FidelityContext.UniqueName("sim2real-evb-ctl-");
        var source = FidelityContext.UniqueName("sim2real.content.");
        var ruleArn = await PutRule(
            ctx,
            events,
            bus,
            ruleName,
            $$"""{"source": ["{{source}}"], "detail-type": ["{{DetailType}}"], "detail": {{PatternDetail}}}""");
        var controlArn = await PutRule(ctx, events, bus, controlName, $$"""{"source": ["{{source}}"]}""");

        var (queueUrl, queueArn) = await CreateQueueWithEventbridgePolicy(ctx, ruleArn);
        var (controlUrl, controlQueueArn) = await CreateQueueWithEventbridgePolicy(ctx, controlArn);
        await PutTarget(ctx, events, bus, ruleName, queueArn);
        await PutTarget(ctx, events, bus, controlName, controlQueueArn);

        // Positive control: a warm-up both rules match is re-emitted until both queues hold it, so the
        // graded batch below goes to two live rules.
        var warm = await WarmUp(ctx, events, bus, source, [queueUrl, controlUrl]);

        var prefix = FidelityContext.UniqueName("m-");
        var markers = Entries.ToDictionary(e => e.Suffix, e => $"{prefix}-{e.Suffix}", StringComparer.Ordinal);
        var resp = await events.PutEventsAsync(new PutEventsRequest
        {
            Entries =
            [
                .. Entries.Select(e => new PutEventsRequestEntry
                {
                    Source = source,
                    DetailType = DetailType,
                    EventBusName = bus,
                    Detail = $$"""{"level": "{{e.Level}}", "where": {"region": "{{e.Region}}"}, "marker": "{{markers[e.Suffix]}}"}""",
                }),
            ],
        });
        Ensure(resp.FailedEntryCount == 0, $"put_events rejected an entry: {resp}");
        Ensure(resp.Entries.Count == Entries.Length, "zip(ENTRIES, resp[\"Entries\"], strict=True)");
        var eventIds = Entries.Zip(resp.Entries).ToDictionary(p => p.First.Suffix, p => p.Second.EventId, StringComparer.Ordinal);

        // The twin receives all four, so the bus has dispatched every entry before absence is graded.
        var control = await Collect(ctx, controlUrl, [.. markers.Values], timeoutS: 40.0);
        control.Remove(warm);
        Ensure(
            control.Keys.Order(StringComparer.Ordinal).SequenceEqual(markers.Values.Order(StringComparer.Ordinal)),
            $"the twin rule's queue received {PyList(control.Keys.Order(StringComparer.Ordinal))}");
        var delivered = await Collect(ctx, queueUrl, [.. Matching.Select(s => markers[s])], timeoutS: 40.0);
        foreach (var (k, v) in await Drain(ctx, queueUrl))
        {
            delivered[k] = v;
        }

        delivered.Remove(warm);
        var want = Matching.Select(s => markers[s]).ToHashSet(StringComparer.Ordinal);
        Ensure(
            want.SetEquals(delivered.Keys),
            $"the content rule delivered {PyList(delivered.Keys.Order(StringComparer.Ordinal))}, expected exactly {PyList(want.Order(StringComparer.Ordinal))}");
        foreach (var suffix in Matching)
        {
            var env = delivered[markers[suffix]];
            var detail = Entries.Single(e => e.Suffix == suffix);
            var expectedWhere = new JsonObject { ["region"] = detail.Region };
            var gotId = (string?)env["id"];
            var gotSource = (string?)env["source"];
            var gotDetailType = (string?)env["detail-type"];
            var gotLevel = (string?)env["detail"]?["level"];
            var gotWhere = env["detail"]?["where"];
            Ensure(
                gotId == eventIds[suffix] && gotSource == source && gotDetailType == DetailType
                    && gotLevel == detail.Level && JsonNode.DeepEquals(gotWhere, expectedWhere),
                $"entry {suffix} arrived as (id, source, detail-type, level, where) " +
                $"('{gotId}', '{gotSource}', '{gotDetailType}', '{gotLevel}', {gotWhere?.ToJsonString() ?? "None"})");
            Ensure(
                ((string?)env["region"], (string?)env["account"]) == (ctx.Region, ctx.Account),
                $"entry {suffix} envelope region/account ('{(string?)env["region"]}', '{(string?)env["account"]}')");
        }

        await events.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = ruleName, EventBusName = bus, Ids = ["sim2real-target"] });
        await events.DeleteRuleAsync(new DeleteRuleRequest { Name = ruleName, EventBusName = bus });
        var gone = $"Rule {ruleName} does not exist on EventBus {bus}.";
        var described = await PollError(
            () => events.DescribeRuleAsync(new DescribeRuleRequest { Name = ruleName, EventBusName = bus }), "ResourceNotFoundException");
        AssertNotFound(described, $"DescribeRule on the deleted rule {ruleName}", gone);
        await PollUntil(
            async () => (await ListRules(events, bus, ruleName)).Select(r => r.Name).ToList(),
            n => n.Count == 0,
            timeoutS: 40.0,
            description: $"deleted rule {ruleName} leaving ListRules");
        var putDead = await Error(
            () => events.PutTargetsAsync(new PutTargetsRequest
            {
                Rule = ruleName, EventBusName = bus, Targets = [new Target { Id = "sim2real-target", Arn = queueArn }],
            }),
            "ResourceNotFoundException",
            $"PutTargets on the deleted rule {ruleName}");
        AssertNotFound(putDead, $"PutTargets on the deleted rule {ruleName}", gone);
    }

    /// <summary>
    /// Re-emit an event every rule here matches until each queue in <c>queue_urls</c> received it;
    /// return its marker.
    /// </summary>
    private static async Task<string> WarmUp(FidelityContext ctx, IAmazonEventBridge events, string bus, string source, string[] queueUrls, double timeoutS = 40.0)
    {
        var warm = FidelityContext.UniqueName("warm-");
        var entry = new PutEventsRequestEntry
        {
            Source = source,
            DetailType = DetailType,
            EventBusName = bus,
            Detail = $$"""{"level": "error", "where": {"region": "eu-warm"}, "marker": "{{warm}}"}""",
        };
        var sqs = ctx.Sqs;
        var pending = queueUrls.ToHashSet(StringComparer.Ordinal);
        var last = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var deadline = FidelityTime.StartNew();
        while (pending.Count > 0)
        {
            if (deadline.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw Fail(
                    $"{pending.Count} rule queue(s) never received the warm-up within {timeoutS:0.0}s; " +
                    $"last markers per pending queue [{string.Join(", ", pending.Select(u => last.TryGetValue(u, out var l) ? PyList(l) : "None"))}]");
            }

            var resp = await events.PutEventsAsync(new PutEventsRequest { Entries = [entry] });
            Ensure(resp.FailedEntryCount == 0, $"put_events rejected the warm-up: {resp}");
            foreach (var url in pending.ToList())
            {
                var msgs = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 })).Messages ?? [];
                foreach (var msg in msgs)
                {
                    await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle });
                }

                last[url] = [.. msgs.Select(m => MarkerOf(JsonNode.Parse(m.Body)!))];
                if (last[url].Contains(warm))
                {
                    pending.Remove(url);
                }
            }
        }

        return warm;
    }

    /// <summary>The error response of one call failing with <c>code</c>; any other outcome fails.</summary>
    private static async Task<AwsError> Error(Func<Task> fn, string code, string what)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is null)
        {
            throw Fail($"{what}: expected {code}, the call succeeded");
        }

        Ensure(error.Code == code, $"{what}: expected {code}, got '{error.Code}'");
        return error;
    }

    private static async Task<string> PutRule(FidelityContext ctx, IAmazonEventBridge events, string bus, string name, string pattern)
    {
        var arn = (await events.PutRuleAsync(new PutRuleRequest
        {
            Name = name,
            EventBusName = bus,
            EventPattern = pattern,
            State = "ENABLED",
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        })).RuleArn;
        ctx.OnCleanup(() => IgnoreNotFound(() => events.DeleteRuleAsync(new DeleteRuleRequest { Name = name, EventBusName = bus })));
        return arn;
    }

    private static async Task PutTarget(FidelityContext ctx, IAmazonEventBridge events, string bus, string rule, string targetArn)
    {
        var resp = await events.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = rule, EventBusName = bus, Targets = [new Target { Id = "sim2real-target", Arn = targetArn }],
        });
        Ensure(resp.FailedEntryCount == 0, $"put_targets failed: {resp}");
        ctx.OnCleanup(() => IgnoreNotFound(
            () => events.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = rule, EventBusName = bus, Ids = ["sim2real-target"] })));
    }

    private static string MarkerOf(JsonNode body) =>
        body["detail"] is JsonObject detail ? (string?)detail["marker"] ?? NoMarker : NoMarker;

    /// <summary>Drain until every marker in `wanted` has arrived; return marker -> envelope of all drained.</summary>
    private static async Task<Dictionary<string, JsonNode>> Collect(FidelityContext ctx, string queueUrl, HashSet<string> wanted, double timeoutS)
    {
        var sqs = ctx.Sqs;
        var seen = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS) && !wanted.IsSubsetOf(seen.Keys))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
            foreach (var msg in resp.Messages ?? [])
            {
                var body = JsonNode.Parse(msg.Body)!;
                seen[MarkerOf(body)] = body;
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
            }
        }

        Ensure(
            wanted.IsSubsetOf(seen.Keys),
            $"queue received {PyList(seen.Keys.Where(m => m != NoMarker).Order(StringComparer.Ordinal))} within {timeoutS:0.0}s, expected {PyList(wanted.Order(StringComparer.Ordinal))}");
        return seen;
    }

    /// <summary>Receive until one empty long poll; return marker -> envelope of every message drained.</summary>
    private static async Task<Dictionary<string, JsonNode>> Drain(FidelityContext ctx, string queueUrl)
    {
        var sqs = ctx.Sqs;
        var seen = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        while (true)
        {
            var msgs = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5,
            })).Messages ?? [];
            if (msgs.Count == 0)
            {
                return seen;
            }

            foreach (var msg in msgs)
            {
                var body = JsonNode.Parse(msg.Body)!;
                seen[MarkerOf(body)] = body;
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
            }
        }
    }

    private static async Task<(string Url, string Arn)> CreateQueueWithEventbridgePolicy(FidelityContext ctx, string ruleArn)
    {
        var sqs = ctx.Sqs;
        var queueUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-evb-queue-"), Tags = new() { ["sim2real"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(queueUrl));
        var queueArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = queueUrl, AttributeNames = ["QueueArn"] })).Attributes["QueueArn"];
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

    private static async Task IgnoreNotFound(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is not null && error.Code != "ResourceNotFoundException")
        {
            throw error.Exception;
        }
    }

    /// <summary>Poll fn until it fails with `code`; return the error response. Raise on timeout.</summary>
    private static async Task<AwsError> PollError(Func<Task> fn, string code, double timeoutS = 40.0, double intervalS = 2.0)
    {
        var deadline = FidelityTime.StartNew();
        var last = "<call succeeded>";
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var error = await FidelityContext.CaptureError(fn);
            if (error is null)
            {
                last = "<call succeeded>";
            }
            else
            {
                last = error.Code ?? "None";
                if (last == code)
                {
                    return error;
                }
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"expected {code}, got '{last}' after {timeoutS:0.0}s");
    }

    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutS, string description, double intervalS = 2.0)
    {
        var deadline = FidelityTime.StartNew();
        T? last = default;
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"{description} not observed within {timeoutS:0.0}s; last saw {last}");
    }

    /// <summary>The response is ResourceNotFoundException with exactly <c>message</c> and an HTTP 4xx.</summary>
    private static void AssertNotFound(AwsError response, string what, string message)
    {
        var status = (int)response.StatusCode;
        Ensure(status is >= 400 and < 500, $"{what} carried HTTP {status}, expected 4xx");
        Ensure(
            (response.Code, response.Message) == ("ResourceNotFoundException", message),
            $"{what}: expected ('ResourceNotFoundException', '{message}'), got ('{response.Code}', '{response.Message}')");
    }

    private static async Task<List<Rule>> ListRules(IAmazonEventBridge events, string bus, string prefix)
    {
        var rules = new List<Rule>();
        string? token = null;
        do
        {
            var page = await events.ListRulesAsync(new ListRulesRequest { NamePrefix = prefix, EventBusName = bus, NextToken = token });
            rules.AddRange(page.Rules ?? []);
            token = page.NextToken;
        }
        while (!string.IsNullOrEmpty(token));

        return rules;
    }

    private static string PyList(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(i => $"'{i}'")) + "]";
}

// Translated from behavioral/events__sqs/check_events_test_event_pattern_verdicts_agree_with_real_delivery.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.EventBridge.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using SqsMessage = Amazon.SQS.Model.Message;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.EventsSqs;

/// <summary>events__sqs.events-test-event-pattern-verdicts-agree-with-real-delivery</summary>
[Category("AwsEvaluationSuite")]
public sealed class EventsTestEventPatternVerdictsAgreeWithRealDelivery
{
    // (row id, the matcher fragment merged into the rule's EventPattern, the event's detail,
    //  the verdict real AWS gives)
    private static readonly List<(string Row, string Matcher, string Detail, bool Want)> Rows =
    [
        ("numeric-in-range", """{"detail": {"n": [{"numeric": [">", 10, "<", 20]}]}}""", """{"n": 15}""", true),
        ("numeric-out-of-range", """{"detail": {"n": [{"numeric": [">", 10, "<", 20]}]}}""", """{"n": 25}""", false),
        ("prefix-match", """{"detail": {"k": [{"prefix": "ab"}]}}""", """{"k": "abcdef"}""", true),
        ("prefix-miss", """{"detail": {"k": [{"prefix": "ab"}]}}""", """{"k": "zbcdef"}""", false),
        ("exists-present", """{"detail": {"opt": [{"exists": true}]}}""", """{"opt": "x"}""", true),
        ("exists-absent", """{"detail": {"opt": [{"exists": true}]}}""", """{"other": "x"}""", false),
        ("anything-but-other-value", """{"detail": {"k": [{"anything-but": ["no"]}]}}""", """{"k": "yes"}""", true),
        ("anything-but-excluded-value", """{"detail": {"k": [{"anything-but": ["no"]}]}}""", """{"k": "no"}""", false),
        ("case-sensitive-differing-case", """{"detail": {"k": ["Abc"]}}""", """{"k": "abc"}""", false),
        ("cross-key-and-both-present", """{"detail": {"a": ["1"], "b": ["2"]}}""", """{"a": "1", "b": "2"}""", true),
    ];

    private const string XsvcPrefix = "sim2real-xsvc-";
    private const string ManagedTagKey = "sim2real:managed";

    /// <summary>
    /// For ten pattern/event pairs, five expected true and five false, TestEventPattern's verdict
    /// matches the expectation and a rule with the same pattern delivers exactly the events it says
    /// match.
    /// </summary>
    [Test]
    [DisplayName("events__sqs.events-test-event-pattern-verdicts-agree-with-real-delivery")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var source = XName("src").Replace('-', '.');

        var verdicts = new Dictionary<string, bool>();
        foreach (var (row, matcher, detail, _) in Rows)
        {
            var pattern = RulePattern(source, row, matcher);
            var @event = new JsonObject
            {
                ["version"] = "0",
                ["id"] = "00000000-0000-0000-0000-000000000000",
                ["detail-type"] = DetailType(row),
                ["source"] = source,
                ["account"] = "123456789012",
                ["time"] = "2026-01-01T00:00:00Z",
                ["region"] = ctx.Region,
                ["resources"] = new JsonArray(),
                ["detail"] = JsonNode.Parse(detail),
            };
            verdicts[row] = (await events.TestEventPatternAsync(new TestEventPatternRequest
            {
                EventPattern = pattern.ToJsonString(),
                Event = @event.ToJsonString(),
            })).Result ?? false;
        }

        var expected = Rows.ToDictionary(r => r.Row, r => r.Want);
        var tepDisagree = expected.Where(kv => verdicts[kv.Key] != kv.Value).ToList();
        Ensure(
            tepDisagree.Count == 0,
            "TestEventPattern disagreed with the verdicts real AWS returns for these rows " +
            $"(row -> (observed, expected)): {{{string.Join(", ", tepDisagree.Select(kv => $"'{kv.Key}': ({Py(verdicts[kv.Key])}, {Py(kv.Value)})"))}}}");

        // One control rule matching the source alone, so it receives EVERY row's event whatever its
        // matcher does. Its arrival is the barrier that licenses each False row's absence assertion.
        var controlQ = await CreateQueue(ctx);
        await PutRuleWithSqsTarget(ctx, new JsonObject { ["source"] = new JsonArray(source) }, controlQ);

        var rowQueues = new Dictionary<string, Queue>();
        foreach (var (row, matcher, _, _) in Rows)
        {
            var q = await CreateQueue(ctx);
            rowQueues[row] = q;
            await PutRuleWithSqsTarget(ctx, RulePattern(source, row, matcher), q);
        }

        var entries = Rows
            .Select(r =>
            {
                var d = JsonNode.Parse(r.Detail)!.AsObject();
                d["row"] = r.Row;
                return new PutEventsRequestEntry { Source = source, DetailType = DetailType(r.Row), Detail = d.ToJsonString() };
            })
            .ToList();

        // Every row rides on EVERY attempt, and both the control queue and all eleven row queues are
        // drained on every attempt. Two barriers have to be satisfied before any absence is asserted:
        // the control rule must have received all ten rows, and every row the engine is expected to
        // match must have received its own event at least once.
        //
        // Both are needed: each of the eleven rules goes live independently and a freshly created rule
        // silently drops events, so the control rule being live says nothing about any row rule.
        var controlSeen = new HashSet<string>();
        var seenPerRow = expected.Keys.ToDictionary(r => r, _ => new HashSet<string>());
        var wanted = expected.Where(kv => kv.Value).Select(kv => kv.Key).ToHashSet();
        for (var i = 0; i < 20; i++)
        {
            await events.PutEventsAsync(new PutEventsRequest { Entries = [.. entries] });
            foreach (var msg in await Collect(ctx, controlQ.Url, windowS: 8.0, maxTotal: 200))
            {
                var got = RowOf(Body(msg));
                if (got is not null && expected.ContainsKey(got))
                {
                    controlSeen.Add(got);
                }
            }

            foreach (var (row, q) in rowQueues)
            {
                foreach (var msg in await Collect(ctx, q.Url, windowS: 3.0, maxTotal: 200))
                {
                    var got = RowOf(Body(msg));
                    if (got is not null)
                    {
                        seenPerRow[row].Add(got);
                    }
                }
            }

            if (controlSeen.SetEquals(expected.Keys) && wanted.IsSubsetOf(seenPerRow.Where(kv => kv.Value.Contains(kv.Key)).Select(kv => kv.Key)))
            {
                break;
            }
        }

        Ensure(
            controlSeen.SetEquals(expected.Keys),
            $"the control rule (pattern {{'source': ['{source}']}}) never received every row, so no " +
            $"absence below is provable: missing {Repr(expected.Keys.Except(controlSeen).Order(StringComparer.Ordinal))}");
        var delivered = seenPerRow.ToDictionary(kv => kv.Key, kv => kv.Value.Contains(kv.Key));
        var neverWarmed = wanted.Where(r => !delivered[r]).Order(StringComparer.Ordinal).ToList();
        Ensure(
            neverWarmed.Count == 0,
            $"rows {Repr(neverWarmed)} are expected to match and never received their own event, so the " +
            "absences on the non-matching rows are not attributable to the matcher -- these rules never " +
            "went live. Per-row messages seen: " +
            $"{{{string.Join(", ", seenPerRow.Select(kv => $"'{kv.Key}': {Repr(kv.Value.Order(StringComparer.Ordinal))}"))}}}");

        var misrouted = seenPerRow
            .Where(kv => kv.Value.Any(r => r != kv.Key))
            .Select(kv => (Row: kv.Key, Others: kv.Value.Where(r => r != kv.Key).Order(StringComparer.Ordinal).ToList()))
            .ToList();
        Ensure(
            misrouted.Count == 0,
            "a row's rule received an event intended for a different row, so the per-row delivery " +
            $"verdicts are not attributable: {{{string.Join(", ", misrouted.Select(x => $"'{x.Row}': {Repr(x.Others)}"))}}}");

        var deliveryDisagree = expected.Where(kv => delivered[kv.Key] != kv.Value).ToList();
        Ensure(
            deliveryDisagree.Count == 0,
            "real delivery disagreed with the verdict real AWS returns for these rows: " +
            $"{{{string.Join(", ", deliveryDisagree.Select(kv => $"'{kv.Key}': {{'expected': {Py(kv.Value)}, 'delivered': {Py(delivered[kv.Key])}}}"))}}}");

        var both = expected.Keys.Where(r => verdicts[r] != delivered[r]).ToList();
        Ensure(
            both.Count == 0,
            $"TestEventPattern and real delivery disagree on {both.Count} of {expected.Count} rows -- the " +
            "synchronous matcher and the delivery path are not the same engine: " +
            $"{{{string.Join(", ", both.Select(r => $"'{r}': {{'test_event_pattern': {Py(verdicts[r])}, 'delivered': {Py(delivered[r])}}}"))}}}");
        Ensure(
            expected.Values.Count(v => v) == 5 && expected.Count == 10,
            $"the table must keep both directions represented, got {expected.Values.Count(v => v)} True of " +
            $"{expected.Count} rows: {{{string.Join(", ", expected.Select(kv => $"'{kv.Key}': {Py(kv.Value)}"))}}}");
    }

    private static string Py(bool b) => b ? "True" : "False";

    private static string Repr(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(i => $"'{i}'")) + "]";

    /// <summary>body["detail"].get("row") when the body is a dict whose detail is a dict.</summary>
    private static string? RowOf(JsonNode? body) =>
        body is JsonObject o && o["detail"] is JsonObject d && d["row"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static string DetailType(string row) => $"row-{row}";

    /// <summary>The row's matcher, scoped to this check's own source and the row's own detail-type.</summary>
    private static JsonObject RulePattern(string source, string row, string matcher)
    {
        var pattern = new JsonObject { ["source"] = new JsonArray(source), ["detail-type"] = new JsonArray(DetailType(row)) };
        foreach (var (key, value) in JsonNode.Parse(matcher)!.AsObject())
        {
            pattern[key] = value?.DeepClone();
        }

        return pattern;
    }

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
    private static async Task<string> PutRuleWithSqsTarget(FidelityContext ctx, JsonObject pattern, Queue queue)
    {
        var events = ctx.Events;
        var name = XName("rule");
        var ruleArn = (await events.PutRuleAsync(new PutRuleRequest { Name = name, EventPattern = pattern.ToJsonString() })).RuleArn;
        ctx.OnCleanup(() => ctx.Events.DeleteRuleAsync(new DeleteRuleRequest { Name = name, Force = true }));
        await SetQueuePolicy(ctx, queue.Url, AllowServiceSend(queue.Arn, "events.amazonaws.com", ruleArn));
        await events.PutTargetsAsync(new PutTargetsRequest { Rule = name, Targets = [new Target { Id = "t1", Arn = queue.Arn }] });
        ctx.OnCleanup(() => ctx.Events.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = name, Ids = ["t1"], Force = true }));
        return name;
    }
}

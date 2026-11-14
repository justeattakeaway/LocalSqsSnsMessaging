// Translated from behavioral/events__sqs/check_put_rule_update_omitting_state_re_enables_a_disabled_rule.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.EventBridge.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using SqsMessage = Amazon.SQS.Model.Message;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.EventsSqs;

/// <summary>events__sqs.put-rule-update-omitting-state-re-enables-a-disabled-rule</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutRuleUpdateOmittingStateReEnablesADisabledRule
{
    private const string XsvcPrefix = "sim2real-xsvc-";
    private const string ManagedTagKey = "sim2real:managed";

    /// <summary>
    /// A PutRule update that omits State turns a DISABLED rule back on, and it delivers again.
    /// PutRule replaces the rule and State defaults to ENABLED; a still-ENABLED control rule on the
    /// same pattern proves the subject's empty queue while disabled was suppression, not lag.
    /// </summary>
    [Test]
    [DisplayName("events__sqs.put-rule-update-omitting-state-re-enables-a-disabled-rule")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var source = XName("src");
        var pattern = new JsonObject { ["source"] = new JsonArray(source) };
        var ctlQ = await CreateQueue(ctx);
        var subjectQ = await CreateQueue(ctx);
        await PutRuleWithSqsTarget(ctx, pattern, ctlQ);
        var subject = await PutRuleWithSqsTarget(ctx, pattern, subjectQ);
        await events.DisableRuleAsync(new DisableRuleRequest { Name = subject });
        var state = (await events.DescribeRuleAsync(new DescribeRuleRequest { Name = subject })).State?.Value;
        Ensure(state == "DISABLED", $"DisableRule left the subject rule '{state}'");

        PutEventsRequestEntry Entry(string marker) =>
            new() { Source = source, DetailType = "Sim2realReenable", Detail = $$"""{"marker": "{{marker}}"}""" };

        var (ctl, _) = await EmitUntil(
            ctx,
            [Entry("while-disabled")],
            ctlQ.Url,
            b => b is JsonObject && DetailString(b, "marker") == "while-disabled");
        Ensure(ctl.Count > 0, "the ENABLED control rule never delivered");
        var leaked = await Collect(ctx, subjectQ.Url, windowS: 15.0);
        Ensure(
            leaked.Count == 0,
            $"the DISABLED subject rule delivered {leaked.Count} message(s) after the control arrived: " +
            $"[{string.Join(", ", leaked.Select(m => PyRepr(Body(m))))}]");

        await events.PutRuleAsync(new PutRuleRequest { Name = subject, EventPattern = pattern.ToJsonString() });
        state = (await events.DescribeRuleAsync(new DescribeRuleRequest { Name = subject })).State?.Value;
        Ensure(
            state == "ENABLED",
            $"a PutRule update that omitted State left the subject rule '{state}', expected ENABLED");

        var (resumed, _) = await EmitUntil(
            ctx,
            [Entry("after-update")],
            subjectQ.Url,
            b => b is JsonObject && DetailString(b, "marker") == "after-update");
        Ensure(resumed.Count > 0, "the re-enabled subject rule never delivered");
        var resumedSource = Body(resumed[0]) is JsonObject o && o["source"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        Ensure(
            resumedSource == source,
            $"the re-enabled rule delivered an event with source {(resumedSource is null ? "None" : $"'{resumedSource}'")}");
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

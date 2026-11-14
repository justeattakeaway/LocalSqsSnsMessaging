// Translated from behavioral/events__sqs/check_enable_rule_direct_call_resumes_delivery.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.EventBridge.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using SqsMessage = Amazon.SQS.Model.Message;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.EventsSqs;

/// <summary>events__sqs.enable-rule-direct-call-resumes-delivery</summary>
[Category("AwsEvaluationSuite")]
public sealed class EnableRuleDirectCallResumesDelivery
{
    private const string XsvcPrefix = "sim2real-xsvc-";
    private const string ManagedTagKey = "sim2real:managed";

    /// <summary>
    /// A DISABLED rule delivers nothing and resumes delivery after a direct EnableRule call; an
    /// always-ENABLED control rule on the same pattern proves the bus stayed live throughout.
    /// </summary>
    [Test]
    [DisplayName("events__sqs.enable-rule-direct-call-resumes-delivery")]
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
        Ensure(state == "DISABLED", $"DisableRule did not take effect: rule {subject} State='{state}'");

        PutEventsRequestEntry Entry(string marker) => new()
        {
            Source = source,
            DetailType = "Sim2realEnableRule",
            Detail = JsonSerializer.Serialize(new { marker }),
        };

        var (ctl, _) = await EmitUntil(
            ctx,
            [Entry("while-disabled")],
            ctlQ.Url,
            b => b is JsonObject o && (string?)o["detail"]?["marker"] == "while-disabled");
        Ensure(ctl.Count > 0, "the control rule (always ENABLED) never delivered; the negative below would prove nothing");
        var leaked = await Collect(ctx, subjectQ.Url, windowS: 20.0);
        Ensure(
            leaked.Count == 0,
            $"the DISABLED subject rule delivered {leaked.Count} message(s) while disabled, after the " +
            $"control had already arrived on the same pattern: [{string.Join(", ", leaked.Select(b => b?.ToJsonString()))}]");

        // The direct call under test -- not a PutRule update.
        await events.EnableRuleAsync(new EnableRuleRequest { Name = subject });
        state = (await events.DescribeRuleAsync(new DescribeRuleRequest { Name = subject })).State?.Value;
        Ensure(state == "ENABLED", $"EnableRule did not take effect: rule {subject} State='{state}'");

        var (resumed, _) = await EmitUntil(
            ctx,
            [Entry("after-enable-rule")],
            subjectQ.Url,
            b => b is JsonObject o && (string?)o["detail"]?["marker"] == "after-enable-rule");
        Ensure(resumed.Count > 0, "the subject rule never delivered after the direct EnableRule call");
        Ensure(
            (string?)resumed[0]?["source"] == source && (string?)resumed[0]?["detail-type"] == "Sim2realEnableRule",
            $"the resumed delivery's envelope was wrong: {resumed[0]?.ToJsonString()}");
    }

    private sealed record Queue(string Name, string Url, string Arn);

    private static Dictionary<string, string> XsvcResourceTags() =>
        new() { [ManagedTagKey] = "true", ["sim2real:prefix"] = XsvcPrefix.TrimEnd('-') };

    /// <summary>A unique, prefix-tagged resource name: sim2real-xsvc-&lt;kind&gt;-&lt;uuid&gt;.</summary>
    private static string XName(string kind) => FidelityContext.UniqueName($"{XsvcPrefix}{kind}-");

    /// <summary>Create an SQS queue (registers delete_queue teardown). Returns {name,url,arn}.</summary>
    private static async Task<Queue> CreateQueue(FidelityContext ctx)
    {
        var name = XName("q");
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Tags = XsvcResourceTags() })).QueueUrl;
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes["QueueArn"];
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        return new Queue(name, url, arn);
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

    private static async Task<List<JsonNode?>> Collect(FidelityContext ctx, string queueUrl, double windowS = 30.0, int maxTotal = 10)
    {
        var sqs = ctx.Sqs;
        var seen = new List<JsonNode?>();
        var started = FidelityTime.StartNew();
        var window = TimeSpan.FromSeconds(windowS);
        while (started.Elapsed < window && seen.Count < maxTotal)
        {
            var wait = Math.Min(5, Math.Max(1, (int)(window - started.Elapsed).TotalSeconds));
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = wait });
            foreach (var msg in resp.Messages ?? [])
            {
                seen.Add(Body(msg));
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
            }
        }

        return seen;
    }

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
    /// Create an EventBridge rule for `pattern`, grant it SendMessage on `queue`, attach it as
    /// target t1. Registers remove_targets + delete_rule teardown. Returns the rule name.
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

    private static async Task<(List<JsonNode?> Matched, List<JsonNode?> Others)> EmitUntil(
        FidelityContext ctx, List<PutEventsRequestEntry> entries, string queueUrl, Func<JsonNode?, bool> predicate,
        int attempts = 10, double pollS = 8.0)
    {
        var events = ctx.Events;
        var others = new List<JsonNode?>();
        for (var i = 0; i < attempts; i++)
        {
            await events.PutEventsAsync(new PutEventsRequest { Entries = [.. entries] });
            var deadline = FidelityTime.StartNew();
            var matched = new List<JsonNode?>();
            while (deadline.Elapsed < TimeSpan.FromSeconds(pollS))
            {
                foreach (var body in await Collect(ctx, queueUrl, windowS: Math.Min(8.0, pollS)))
                {
                    (predicate(body) ? matched : others).Add(body);
                }
            }

            if (matched.Count > 0)
            {
                return (matched, others);
            }
        }

        throw Fail(
            $"control event never delivered to {queueUrl} after {attempts} PutEvents attempts " +
            $"(non-matching messages seen meanwhile: {others.Count})");
    }
}

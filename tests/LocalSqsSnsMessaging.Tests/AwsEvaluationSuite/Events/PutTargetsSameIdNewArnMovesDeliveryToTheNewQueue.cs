// Translated from behavioral/events/check_put_targets_same_id_new_arn_moves_delivery_to_the_new_queue.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using Tag = Amazon.EventBridge.Model.Tag;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.put-targets-same-id-new-arn-moves-delivery-to-the-new-queue</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutTargetsSameIdNewArnMovesDeliveryToTheNewQueue
{
    private const string Check = "put-targets-same-id-new-arn-moves-delivery-to-the-new-queue";
    private const string DetailType = "sim2real-target-upsert";
    private const string TargetId = "sim2real-target";

    /// <summary>
    /// PutTargets reusing a target Id with queue B and an InputTransformer replaces the target: one
    /// target lists with B's ARN and the transformer, and the next event reaches B transformed, not A.
    /// </summary>
    [Test]
    [DisplayName("events.put-targets-same-id-new-arn-moves-delivery-to-the-new-queue")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var rule = FidelityContext.UniqueName("sim2real-evb-upsert-");
        var source = FidelityContext.UniqueName("sim2real.upsert.");
        var ruleArn = (await events.PutRuleAsync(new PutRuleRequest
        {
            Name = rule,
            EventPattern = $$"""{"source": ["{{source}}"], "detail-type": ["{{DetailType}}"]}""",
            State = RuleState.ENABLED,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        })).RuleArn;
        ctx.OnCleanup(() => events.DeleteRuleAsync(new DeleteRuleRequest { Name = rule }));
        var (aUrl, aArn) = await CreateQueueForRule(ctx, ruleArn, "sim2real-evb-upsert-a-");
        var (bUrl, bArn) = await CreateQueueForRule(ctx, ruleArn, "sim2real-evb-upsert-b-");

        var resp = await events.PutTargetsAsync(new PutTargetsRequest { Rule = rule, Targets = [new Target { Id = TargetId, Arn = aArn }] });
        Ensure(resp.FailedEntryCount == 0, $"PutTargets (queue A) failed: {resp.FailedEntries?.Count} failed entries");
        ctx.OnCleanup(() => events.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = rule, Ids = [TargetId] }));
        await Converge(ctx, source, aUrl, "queue-a");

        const string template = """{"via": "queue-b", "marker": <m>}""";
        resp = await events.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = rule,
            Targets =
            [
                new Target
                {
                    Id = TargetId,
                    Arn = bArn,
                    InputTransformer = new InputTransformer
                    {
                        InputPathsMap = new() { ["m"] = "$.detail.marker" },
                        InputTemplate = template,
                    },
                },
            ],
        });
        Ensure(resp.FailedEntryCount == 0, $"PutTargets (same Id, queue B) failed: {resp.FailedEntries?.Count} failed entries");
        var targets = (await events.ListTargetsByRuleAsync(new ListTargetsByRuleRequest { Rule = rule })).Targets
            ?? throw new KeyNotFoundException("'Targets'");
        Ensure(targets.Count == 1, $"after the same-Id PutTargets the rule lists {targets.Count} targets, expected 1");
        Ensure(targets[0].Id == TargetId, $"the remaining target has Id '{targets[0].Id}'");
        Ensure(targets[0].Arn == bArn, "the same-Id PutTargets left the target pointing at queue A");
        var it = targets[0].InputTransformer;
        Ensure(
            it is not null && it.InputTemplate == template && it.InputPathsMap is { Count: 1 } map && map.GetValueOrDefault("m") == "$.detail.marker",
            $"target InputTransformer reads back {Show(it)}, expected {{'InputPathsMap': {{'m': '$.detail.marker'}}, 'InputTemplate': '{template}'}}");

        await Converge(ctx, source, bUrl, "queue-b");
        await Drain(ctx, aUrl, 5.0);

        var final = $"after-upsert-{Guid.NewGuid():N}";
        var started = FidelityTime.StartNew();
        await Put(ctx, source, final);
        var (body, _) = await AwaitMarker(ctx, bUrl, final, 120.0);
        Ensure(body is not null, "the event put after the upsert never reached queue B");
        Ensure(
            JsonNode.DeepEquals(body, new JsonObject { ["via"] = "queue-b", ["marker"] = final }),
            $"queue B received {body.ToJsonString()}, not the transformer's rendering");
        var stray = await Drain(ctx, aUrl, Math.Max(15.0, 3 * started.Elapsed.TotalSeconds));
        Ensure(!stray.ContainsKey(final), "the event put after the upsert was also delivered to the replaced queue A");
    }

    /// <summary>An SQS queue whose policy lets EventBridge deliver from <c>rule_arn</c> only.</summary>
    private static async Task<(string Url, string Arn)> CreateQueueForRule(FidelityContext ctx, string ruleArn, string prefix)
    {
        var sqs = ctx.Sqs;
        var queueUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName(prefix),
            Tags = new() { ["sim2real"] = Check },
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

    private static async Task Put(FidelityContext ctx, string source, string marker)
    {
        var resp = await ctx.Events.PutEventsAsync(new PutEventsRequest
        {
            Entries = [new PutEventsRequestEntry { Source = source, DetailType = DetailType, Detail = $$"""{"marker": "{{marker}}"}""" }],
        });
        Ensure(resp.FailedEntryCount == 0, $"PutEvents refused an entry: {string.Join(", ", (resp.Entries ?? []).Select(e => $"{{'ErrorCode': '{e.ErrorCode}'}}"))}");
    }

    /// <summary>The marker of a delivered message: the envelope's detail.marker, or a transformed body's.</summary>
    private static string? MarkerOf(JsonNode? body)
    {
        var detail = body?["detail"];
        if (detail is JsonObject d && d.ContainsKey("marker"))
        {
            return (string?)d["marker"];
        }

        return (string?)body?["marker"];
    }

    /// <summary>Receive and delete everything on <c>queue_url</c> for <c>window_s</c> seconds; marker -> body.</summary>
    private static async Task<Dictionary<string, JsonNode?>> Drain(FidelityContext ctx, string queueUrl, double windowS)
    {
        var sqs = ctx.Sqs;
        var seen = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        var started = FidelityTime.StartNew();
        while (started.Elapsed < TimeSpan.FromSeconds(windowS))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                // json.loads raises on a body that is not JSON, as JsonNode.Parse does.
                var body = JsonNode.Parse(msg.Body);
                var marker = MarkerOf(body);
                if (!string.IsNullOrEmpty(marker))
                {
                    seen[marker] = body;
                }
            }
        }

        return seen;
    }

    /// <summary>Drain <c>queue_url</c> until <c>marker</c> arrives or <c>timeout_s</c> passes; (its body or None, all seen).</summary>
    private static async Task<(JsonNode? Body, Dictionary<string, JsonNode?> Seen)> AwaitMarker(FidelityContext ctx, string queueUrl, string marker, double timeoutS)
    {
        var seen = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        var started = FidelityTime.StartNew();
        while (started.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            foreach (var (k, v) in await Drain(ctx, queueUrl, 4.0))
            {
                seen[k] = v;
            }

            if (seen.TryGetValue(marker, out var found))
            {
                return (found, seen);
            }
        }

        return (null, seen);
    }

    /// <summary>
    /// Send probes from <c>source</c> until <c>streak</c> in a row reach <c>queue_url</c>: a rule or target
    /// update is eventually consistent, so the claim is graded only once the update is stably live.
    /// </summary>
    private static async Task<Dictionary<string, JsonNode?>> Converge(FidelityContext ctx, string source, string queueUrl, string label, int attempts = 20, int streak = 3)
    {
        var seen = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        var runOf = 0;
        for (var i = 0; i < attempts; i++)
        {
            var probe = $"{label}-probe-{i}-{Guid.NewGuid():N}";
            await Put(ctx, source, probe);
            var (body, got) = await AwaitMarker(ctx, queueUrl, probe, 15.0);
            foreach (var (k, v) in got)
            {
                seen[k] = v;
            }

            runOf = body is not null ? runOf + 1 : 0;
            if (runOf >= streak)
            {
                return seen;
            }
        }

        throw Fail($"{label} probes did not reach their queue {streak} times in a row within {attempts} attempts");
    }

    private static string Show(InputTransformer? it) =>
        it is null
            ? "None"
            : $"{{'InputPathsMap': {{{string.Join(", ", (it.InputPathsMap ?? []).Select(kv => $"'{kv.Key}': '{kv.Value}'"))}}}, 'InputTemplate': '{it.InputTemplate}'}}";
}

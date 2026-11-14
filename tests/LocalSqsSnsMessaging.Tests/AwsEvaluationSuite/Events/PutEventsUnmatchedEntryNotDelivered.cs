// Translated from behavioral/events/check_put_events_unmatched_entry_not_delivered.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.put-events-unmatched-entry-not-delivered</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutEventsUnmatchedEntryNotDelivered
{
    private const string Check = "put-events-unmatched-entry-not-delivered";

    /// <summary>
    /// A PutEvents entry that does not match the rule's pattern is never delivered to the
    /// rule's target, while a matching entry sent in the SAME batch (positive control) is -- this
    /// distinguishes accepted-but-undelivered from actually delivered.
    /// </summary>
    [Test]
    [DisplayName("events.put-events-unmatched-entry-not-delivered")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;

        var ruleName = FidelityContext.UniqueName("sim2real-evb-rule-");
        var matchedSource = FidelityContext.UniqueName("sim2real.match.");
        var unmatchedSource = FidelityContext.UniqueName("sim2real.nomatch.");
        const string detailType = "sim2real-selective-type";

        var ruleArn = (await events.PutRuleAsync(new PutRuleRequest
        {
            Name = ruleName,
            EventPattern = $$"""{"source": ["{{matchedSource}}"], "detail-type": ["{{detailType}}"]}""",
            State = RuleState.ENABLED,
            Tags = [new Amazon.EventBridge.Model.Tag { Key = "sim2real", Value = Check }],
        })).RuleArn;
        ctx.OnCleanup(() => events.DeleteRuleAsync(new DeleteRuleRequest { Name = ruleName }));

        var (queueUrl, queueArn) = await CreateQueueWithEventbridgePolicy(ctx, ruleArn);

        // IAM/SQS resource-policy changes can take a little while to propagate; give EventBridge
        // a head start before registering the target so the very first delivery attempt doesn't
        // race a not-yet-visible policy (which some backends will not retry indefinitely).
        await WaitForPolicyVisible(ctx, queueUrl);

        var putTargetsResp = await events.PutTargetsAsync(new PutTargetsRequest { Rule = ruleName, Targets = [new Target { Id = "sim2real-target", Arn = queueArn }] });
        Ensure(putTargetsResp.FailedEntryCount == 0, $"put_targets failed: {putTargetsResp.FailedEntryCount}");
        ctx.OnCleanup(() => events.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = ruleName, Ids = ["sim2real-target"] }));

        var targets = (await events.ListTargetsByRuleAsync(new ListTargetsByRuleRequest { Rule = ruleName })).Targets
            ?? throw new KeyNotFoundException("'Targets'");
        Ensure(targets.Any(t => t.Arn == queueArn), "target was not registered on the rule");

        var matchedMarker = FidelityContext.UniqueName("matched-marker-");
        var unmatchedMarker = FidelityContext.UniqueName("unmatched-marker-");

        var putEventsResp = await events.PutEventsAsync(new PutEventsRequest
        {
            Entries =
            [
                new PutEventsRequestEntry
                {
                    Source = matchedSource,
                    DetailType = detailType,
                    Detail = $$"""{"marker": "{{matchedMarker}}"}""",
                },
                new PutEventsRequestEntry
                {
                    // Same DetailType, but a Source the rule's pattern does not select.
                    Source = unmatchedSource,
                    DetailType = detailType,
                    Detail = $$"""{"marker": "{{unmatchedMarker}}"}""",
                },
            ],
        });
        Ensure(putEventsResp.FailedEntryCount == 0, $"put_events failed: {putEventsResp.FailedEntryCount}");

        var seenMarkers = await DrainAndHold(ctx, queueUrl, matchedMarker, timeoutS: 120.0);
        Ensure(seenMarkers.Contains(matchedMarker), "matching entry (positive control) was never delivered");
        Ensure(
            !seenMarkers.Contains(unmatchedMarker),
            "non-matching entry was delivered even though it does not satisfy the rule's EventPattern");
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
    /// Poll GetQueueAttributes until the just-set resource Policy is readable back, as a
    /// (best-effort) signal that the policy write has propagated before we register the target.
    /// </summary>
    private static async Task WaitForPolicyVisible(FidelityContext ctx, string queueUrl, double timeoutS = 30.0)
    {
        var sqs = ctx.Sqs;
        var started = FidelityTime.StartNew();
        while (started.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var attrs = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = queueUrl, AttributeNames = ["Policy"] })).Attributes
                ?? throw new KeyNotFoundException("'Attributes'");
            if (!string.IsNullOrEmpty(attrs.GetValueOrDefault("Policy")))
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>
    /// Drain until the positive control (target_marker) is seen, measuring its own delivery
    /// latency L, then keep draining for a further &gt;=3L (floor 15s) before declaring anything
    /// else absent -- a stop-the-instant-the-positive-arrives window would let a target that
    /// delivers the non-matching entry with extra lag escape detection. Finishes with an
    /// independent observable (queue depth) rather than trusting an empty receive alone.
    /// </summary>
    private static async Task<HashSet<string>> DrainAndHold(FidelityContext ctx, string queueUrl, string targetMarker, double timeoutS)
    {
        var sqs = ctx.Sqs;
        var seen = new HashSet<string>();
        var clock = FidelityTime.StartNew();

        async Task DrainUntil(TimeSpan deadline, string? stopOnceSeen)
        {
            while (clock.Elapsed < deadline)
            {
                var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
                foreach (var msg in resp.Messages ?? [])
                {
                    var body = JsonNode.Parse(msg.Body)!.AsObject();
                    await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                    var marker = body["detail"] is JsonObject detail && detail["marker"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
                    if (!string.IsNullOrEmpty(marker))
                    {
                        seen.Add(marker);
                    }
                }

                if (stopOnceSeen is not null && seen.Contains(stopOnceSeen))
                {
                    return;
                }
            }
        }

        var start = clock.Elapsed;
        await DrainUntil(start + TimeSpan.FromSeconds(timeoutS), stopOnceSeen: targetMarker);
        if (!seen.Contains(targetMarker))
        {
            throw Fail($"target marker {targetMarker} never arrived within {timeoutS}s");
        }

        var latencyS = (clock.Elapsed - start).TotalSeconds;
        var holdS = Math.Max(15.0, latencyS * 3);
        await DrainUntil(clock.Elapsed + TimeSpan.FromSeconds(holdS), stopOnceSeen: null);

        var attrs = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            AttributeNames = ["ApproximateNumberOfMessages", "ApproximateNumberOfMessagesNotVisible"],
        })).Attributes ?? throw new KeyNotFoundException("'Attributes'");
        Ensure(
            attrs["ApproximateNumberOfMessages"] == "0" && attrs["ApproximateNumberOfMessagesNotVisible"] == "0",
            $"queue still reports pending messages after the {holdS:0.0}s hold window: {{{string.Join(", ", attrs.Select(kv => $"'{kv.Key}': '{kv.Value}'"))}}}");
        return seen;
    }
}

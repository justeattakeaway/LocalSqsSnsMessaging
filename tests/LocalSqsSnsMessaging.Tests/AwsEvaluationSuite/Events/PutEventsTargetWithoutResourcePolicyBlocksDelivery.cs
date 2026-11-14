// Translated from behavioral/events/check_put_events_target_without_resource_policy_blocks_delivery.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.put-events-target-without-resource-policy-blocks-delivery</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutEventsTargetWithoutResourcePolicyBlocksDelivery
{
    private const string Check = "put-events-target-without-resource-policy-blocks-delivery";

    /// <summary>
    /// On one rule, an SQS target without a queue policy granting events.amazonaws.com never receives the
    /// matching event while a sibling queue with that policy does: PutTargets alone grants no delivery.
    /// </summary>
    [Test]
    [DisplayName("events.put-events-target-without-resource-policy-blocks-delivery")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EventBridgeUngrantedTargets, "Kept deliberately: a queue with no policy still receives EventBridge deliveries, as most test setups add none; AWS refuses a queue that grants events.amazonaws.com nothing")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var sqs = ctx.Sqs;

        var ruleName = FidelityContext.UniqueName("sim2real-evb-rule-");
        var source = FidelityContext.UniqueName("sim2real.policytest.");
        const string detailType = "sim2real-policy-type";

        var ruleArn = (await events.PutRuleAsync(new PutRuleRequest
        {
            Name = ruleName,
            EventPattern = $$"""{"source": ["{{source}}"], "detail-type": ["{{detailType}}"]}""",
            State = RuleState.ENABLED,
            Tags = [new Amazon.EventBridge.Model.Tag { Key = "sim2real-check", Value = Check }],
        })).RuleArn;
        ctx.OnCleanup(() => events.DeleteRuleAsync(new DeleteRuleRequest { Name = ruleName }));

        // Negative subject: queue with NO resource policy granting EventBridge send permission.
        var noPolicyUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-nopolicy-"),
            Tags = new() { ["sim2real-check"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(noPolicyUrl));
        var noPolicyArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = noPolicyUrl, AttributeNames = ["QueueArn"] }))
            .Attributes["QueueArn"];

        // Positive control: an otherwise-identical queue WITH the policy.
        var (policyUrl, policyArn) = await CreateQueueWithEventbridgePolicy(ctx, ruleArn);

        var putTargetsResp = await events.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = ruleName,
            Targets =
            [
                new Target { Id = "no-policy-target", Arn = noPolicyArn },
                new Target { Id = "with-policy-target", Arn = policyArn },
            ],
        });
        ctx.OnCleanup(() => events.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = ruleName, Ids = ["no-policy-target", "with-policy-target"] }));
        // EventBridge does not validate SQS resource-policy permissions synchronously at
        // PutTargets time -- both targets are accepted.
        Ensure(
            putTargetsResp.FailedEntryCount == 0,
            $"expected PutTargets to accept both targets synchronously (FailedEntryCount=0), " +
            $"got FailedEntryCount={putTargetsResp.FailedEntryCount} entries={string.Join(", ", (putTargetsResp.FailedEntries ?? []).Select(f => $"({f.TargetId}, {f.ErrorCode}, {f.ErrorMessage})"))}");

        var marker = FidelityContext.UniqueName("marker-");
        await events.PutEventsAsync(new PutEventsRequest
        {
            Entries = [new PutEventsRequestEntry { Source = source, DetailType = detailType, Detail = $$"""{"marker": "{{marker}}"}""" }],
        });

        // Positive control must actually receive the event.
        var delivered = await PollForMarker(ctx, policyUrl, marker, timeoutS: 60.0);
        Ensure(
            (string?)delivered["detail"]!["marker"] == marker,
            $"positive control queue {policyUrl} received an event but its marker did not " +
            $"match: expected '{marker}', got '{delivered["detail"]!["marker"]}'");

        // Negative subject must never receive it, even after the control has already arrived.
        var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = noPolicyUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
        Ensure(
            (resp.Messages ?? []).Count == 0,
            "event was delivered to an SQS target lacking a resource policy granting EventBridge access");
    }

    private static async Task<(string QueueUrl, string QueueArn)> CreateQueueWithEventbridgePolicy(FidelityContext ctx, string ruleArn)
    {
        var sqs = ctx.Sqs;
        var queueName = FidelityContext.UniqueName("sim2real-evb-queue-");
        var queueUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = queueName, Tags = new() { ["sim2real-check"] = Check } })).QueueUrl;
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

    private static async Task<JsonObject> PollForMarker(FidelityContext ctx, string queueUrl, string marker, double timeoutS = 60.0)
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
                if (body["detail"] is JsonObject detail && detail["marker"] is JsonValue v && v.TryGetValue<string>(out var s) && s == marker)
                {
                    return body;
                }
            }
        }

        throw Fail($"event with marker {marker} was never delivered to {queueUrl} within {timeoutS}s");
    }
}

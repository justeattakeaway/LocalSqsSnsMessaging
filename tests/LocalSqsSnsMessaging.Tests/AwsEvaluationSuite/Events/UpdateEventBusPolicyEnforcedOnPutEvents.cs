// Translated from behavioral/events/check_update_event_bus_policy_enforced_on_put_events.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.update-event-bus-policy-enforced-on-put-events</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateEventBusPolicyEnforcedOnPutEvents
{
    /// <summary>
    /// An explicit Deny in the default bus's resource policy (set with PutPermission) makes PutEvents
    /// fail as a whole with AccessDeniedException and the event is never delivered to the rule's queue;
    /// restoring the original policy restores delivery.
    /// </summary>
    [Test]
    [DisplayName("events.update-event-bus-policy-enforced-on-put-events")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var sqs = ctx.Sqs;
        var accountId = ctx.Account;
        var busArn = $"arn:aws:events:{ctx.Region}:{accountId}:event-bus/default";

        var queueUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-q-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(queueUrl));
        var queueArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = queueUrl, AttributeNames = ["QueueArn"],
        })).Attributes["QueueArn"];

        var source = FidelityContext.UniqueName("sim2real.policytest.");
        var ruleName = FidelityContext.UniqueName("sim2real-behav-rule-");
        var ruleArn = (await events.PutRuleAsync(new PutRuleRequest
        {
            Name = ruleName,
            EventPattern = $$"""{"source": ["{{source}}"]}""",
        })).RuleArn;
        ctx.OnCleanup(() => events.DeleteRuleAsync(new DeleteRuleRequest { Name = ruleName }));

        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            Attributes = new Dictionary<string, string>
            {
                ["Policy"] = new JsonObject
                {
                    ["Version"] = "2012-10-17",
                    ["Statement"] = new JsonArray(new JsonObject
                    {
                        ["Sid"] = "AllowEventsToSendMessage",
                        ["Effect"] = "Allow",
                        ["Principal"] = new JsonObject { ["Service"] = "events.amazonaws.com" },
                        ["Action"] = "sqs:SendMessage",
                        ["Resource"] = queueArn,
                        ["Condition"] = new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = ruleArn } },
                    }),
                }.ToJsonString(),
            },
        });

        const string targetId = "sim2real-target";
        await events.PutTargetsAsync(new PutTargetsRequest { Rule = ruleName, Targets = [new Target { Id = targetId, Arn = queueArn }] });
        ctx.OnCleanup(() => events.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = ruleName, Ids = [targetId] }));

        // Positive control #1: with no restrictive policy yet, PutEvents succeeds and delivers.
        await PutAndConfirmDelivery(events, sqs, queueUrl, source, "before-policy");

        // PutPermission(Policy=...) is the only way to express a Deny, and it replaces the whole bus
        // policy; snapshot the account-singleton policy and restore exactly that on cleanup.
        var originalPolicy = (await events.DescribeEventBusAsync(new DescribeEventBusRequest { Name = "default" })).Policy;
        var statements = new JsonArray();
        if (!string.IsNullOrEmpty(originalPolicy))
        {
            foreach (var s in JsonNode.Parse(originalPolicy)!["Statement"]!.AsArray())
            {
                if ((string?)s?["Sid"] != "DenyOwnAccountPutEventsForThisTest")
                {
                    statements.Add(s?.DeepClone());
                }
            }
        }

        // An explicit resource-policy Deny overrides the allowing identity policy. The events:source
        // condition scopes it to this check's unique source so other default-bus traffic is unaffected.
        statements.Add(new JsonObject
        {
            ["Sid"] = "DenyOwnAccountPutEventsForThisTest",
            ["Effect"] = "Deny",
            ["Principal"] = new JsonObject { ["AWS"] = $"arn:aws:iam::{accountId}:root" },
            ["Action"] = "events:PutEvents",
            ["Resource"] = busArn,
            ["Condition"] = new JsonObject { ["StringEquals"] = new JsonObject { ["events:source"] = source } },
        });
        var denyPolicy = new JsonObject { ["Version"] = "2012-10-17", ["Statement"] = statements }.ToJsonString();
        await events.PutPermissionAsync(new PutPermissionRequest { Policy = denyPolicy });
        ctx.OnCleanup(() => RestoreBusPolicyQuiet(events, originalPolicy));

        var described = await events.DescribeEventBusAsync(new DescribeEventBusRequest { Name = "default" });
        Ensure((described.Policy ?? "").Contains("DenyOwnAccountPutEventsForThisTest", StringComparison.Ordinal), "assert 'DenyOwnAccountPutEventsForThisTest' in Policy");

        var blockedDetail = JsonSerializer.Serialize(new { stage = "after-policy", nonce = FidelityContext.UniqueName("n-") });
        var errorCode = await PutUntilDenied(events, source, blockedDetail, timeoutS: 60.0);
        Ensure(errorCode == "AccessDeniedException", errorCode);

        // A PutEvents made before the Deny fully propagated can legitimately deliver; discard those.
        await DrainMessages(sqs, queueUrl, timeoutS: 5);

        // With enforcement steady, a fresh event must be denied and never reach the queue.
        var steadyNonce = FidelityContext.UniqueName("n-");
        var steadyDetail = JsonSerializer.Serialize(new { stage = "after-policy-steady", nonce = steadyNonce });
        var steadyErrorCode = await PutUntilDenied(events, source, steadyDetail, timeoutS: 15.0);
        Ensure(steadyErrorCode == "AccessDeniedException", steadyErrorCode);

        var leaked = await DrainMessages(sqs, queueUrl, timeoutS: 8);
        var matching = leaked.Where(m => m.Contains(steadyNonce, StringComparison.Ordinal)).ToList();
        Ensure(matching.Count == 0, $"message delivered despite explicit-deny bus policy: [{string.Join(", ", matching)}]");

        // Positive control #2: with the Deny removed, delivery resumes.
        await RestoreBusPolicy(events, originalPolicy);
        await PutAndConfirmDelivery(events, sqs, queueUrl, source, "after-removal");
    }

    /// <summary>Poll PutEvents until the whole call raises AccessDeniedException.</summary>
    /// <remarks>A bus resource-policy Deny rejects the entire request, not individual entries.</remarks>
    private static async Task<string> PutUntilDenied(AmazonEventBridgeClient events, string source, string detailJson, double timeoutS)
    {
        var started = FidelityTime.StartNew();
        AwsError? lastExc = null;
        PutEventsResponse? lastResp = null;
        while (started.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var (resp, error) = await FidelityContext.Capture(() => events.PutEventsAsync(new PutEventsRequest
            {
                Entries = [new PutEventsRequestEntry { Source = source, DetailType = "sim2real-test", Detail = detailJson }],
            }));
            if (error is null)
            {
                lastResp = resp;
                lastExc = null;
            }
            else
            {
                lastExc = error;
                if (error.Code == "AccessDeniedException")
                {
                    return error.Code;
                }
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(3.0));
        }

        throw Fail(
            $"deny policy was never enforced within {timeoutS:F1}s: " +
            $"last_exc={lastExc?.ToString() ?? "None"} last_resp={(lastResp is null ? "None" : $"{{'FailedEntryCount': {lastResp.FailedEntryCount}}}")}");
    }

    private static async Task PutAndConfirmDelivery(AmazonEventBridgeClient events, AmazonSQSClient sqs, string queueUrl, string source, string stage)
    {
        var nonce = FidelityContext.UniqueName("n-");
        var detailJson = JsonSerializer.Serialize(new { stage, nonce });

        // Policy add/remove can take a few seconds to propagate; retry past a transient
        // AccessDeniedException (raised as a ClientError, or reported as a resp failure)
        // instead of asserting on the very first attempt.
        var putStarted = FidelityTime.StartNew();
        PutEventsResponse? resp = null;
        while (putStarted.Elapsed < TimeSpan.FromSeconds(30.0))
        {
            var (attempt, error) = await FidelityContext.Capture(() => events.PutEventsAsync(new PutEventsRequest
            {
                Entries = [new PutEventsRequestEntry { Source = source, DetailType = "sim2real-test", Detail = detailJson }],
            }));
            if (error is not null)
            {
                await FidelityTime.Delay(TimeSpan.FromSeconds(3.0));
                continue;
            }

            resp = attempt;
            if (resp!.FailedEntryCount == 0)
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(3.0));
        }

        Ensure(
            resp?.FailedEntryCount == 0,
            $"[{string.Join(", ", (resp?.Entries ?? []).Select(e => $"{e.EventId} {e.ErrorCode} {e.ErrorMessage}"))}]");

        var started = FidelityTime.StartNew();
        while (started.Elapsed < TimeSpan.FromSeconds(30.0))
        {
            var received = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5,
            })).Messages ?? [];
            foreach (var msg in received)
            {
                var body = JsonNode.Parse(msg.Body);
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                if ((string?)body?["detail"]?["nonce"] == nonce)
                {
                    return;
                }
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(0.5));
        }

        throw Fail($"stage='{stage}' event was never delivered to the queue");
    }

    private static async Task<List<string>> DrainMessages(AmazonSQSClient sqs, string queueUrl, double timeoutS)
    {
        var output = new List<string>();
        var started = FidelityTime.StartNew();
        while (started.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var received = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 2,
            })).Messages ?? [];
            foreach (var msg in received)
            {
                output.Add(msg.Body);
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
            }

            if (received.Count == 0)
            {
                await FidelityTime.Delay(TimeSpan.FromSeconds(0.5));
            }
        }

        return output;
    }

    /// <summary>
    /// Put the default bus's policy back exactly as found, rather than wiping it --
    /// the bus's policy is an account-global singleton this check does not own.
    /// </summary>
    private static async Task RestoreBusPolicy(AmazonEventBridgeClient events, string? originalPolicy)
    {
        if (!string.IsNullOrEmpty(originalPolicy))
        {
            await events.PutPermissionAsync(new PutPermissionRequest { Policy = originalPolicy });
        }
        else
        {
            await events.RemovePermissionAsync(new Amazon.EventBridge.Model.RemovePermissionRequest { RemoveAllPermissions = true });
        }
    }

    private static async Task RestoreBusPolicyQuiet(AmazonEventBridgeClient events, string? originalPolicy)
    {
        try
        {
            await RestoreBusPolicy(events, originalPolicy);
        }
        catch (AmazonServiceException exc) when (exc.ErrorCode == "ResourceNotFoundException")
        {
        }
    }
}

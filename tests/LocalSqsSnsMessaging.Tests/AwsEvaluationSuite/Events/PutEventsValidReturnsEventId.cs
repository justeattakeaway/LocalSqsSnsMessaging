// Translated from behavioral/events/check_put_events_valid_returns_event_id.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.EventBridge.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.put-events-valid-returns-event-id</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutEventsValidReturnsEventId
{
    /// <summary>
    /// PutEvents with a well-formed entry succeeds, returns a real EventId, and that
    /// exact id shows up on an independent read at the destination -- the id a rule-matched
    /// SQS target receives, not merely the value the mutating call echoed back.
    /// </summary>
    [Test]
    [DisplayName("events.put-events-valid-returns-event-id")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var sqs = ctx.Sqs;

        var queueUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-q-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(queueUrl));
        var queueArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = queueUrl, AttributeNames = ["QueueArn"] }))
            .Attributes["QueueArn"];

        var source = FidelityContext.UniqueName("sim2real.valid.");
        var ruleName = FidelityContext.UniqueName("sim2real-behav-rule-");
        var ruleArn = (await events.PutRuleAsync(new PutRuleRequest { Name = ruleName, EventPattern = $$"""{"source": ["{{source}}"]}""" })).RuleArn;
        ctx.OnCleanup(() => events.DeleteRuleAsync(new DeleteRuleRequest { Name = ruleName }));

        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            Attributes = new()
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

        var resp = await events.PutEventsAsync(new PutEventsRequest
        {
            Entries = [new PutEventsRequestEntry { Source = source, DetailType = "test", Detail = "{}" }],
        });
        Ensure((resp.FailedEntryCount ?? 0) == 0, $"expected success, got {resp.FailedEntryCount}");
        var entry = (resp.Entries ?? throw new KeyNotFoundException("'Entries'"))[0];
        var eventId = entry.EventId;
        Ensure(!string.IsNullOrEmpty(eventId), "successful entry must have an EventId");

        var envelope = await PollForId(sqs, queueUrl, eventId, timeoutS: 60.0);
        Ensure(
            (string?)envelope["id"] == eventId,
            $"delivered envelope id '{envelope["id"]}' != returned EventId '{eventId}'");
        Ensure((string?)envelope["source"] == source, $"expected source '{source}', got '{envelope["source"]}'");
    }

    private static async Task<JsonObject> PollForId(AmazonSQSClient sqs, string queueUrl, string eventId, double timeoutS = 60.0)
    {
        var started = FidelityTime.StartNew();
        while (started.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
            foreach (var msg in resp.Messages ?? [])
            {
                var body = JsonNode.Parse(msg.Body)!.AsObject();
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                if ((string?)body["id"] == eventId)
                {
                    return body;
                }
            }
        }

        throw Fail($"event {eventId} was never delivered to {queueUrl} within {timeoutS}s");
    }
}

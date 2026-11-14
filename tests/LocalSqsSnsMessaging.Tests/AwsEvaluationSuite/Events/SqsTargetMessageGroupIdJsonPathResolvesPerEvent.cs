// Translated from behavioral/events/check_sqs_target_message_group_id_json_path_resolves_per_event.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Text.Json.Nodes;
using Amazon.EventBridge.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using Tag = Amazon.EventBridge.Model.Tag;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.sqs-target-message-group-id-json-path-resolves-per-event</summary>
[Category("AwsEvaluationSuite")]
public sealed class SqsTargetMessageGroupIdJsonPathResolvesPerEvent
{
    private const string Check = "sqs-target-message-group-id-json-path-resolves-per-event";
    private const double DeliveryTimeoutS = 120.0;
    private const double ResendS = 15.0;

    /// <summary>
    /// SqsParameters.MessageGroupId holding a JSON path ('$.detail.group') is evaluated per
    /// event: the received message's MessageGroupId equals that event's own detail.group value,
    /// not a fixed string.
    ///
    /// Reference: https://docs.aws.amazon.com/eventbridge/latest/APIReference/API_SqsParameters.html
    /// </summary>
    [Test]
    [DisplayName("events.sqs-target-message-group-id-json-path-resolves-per-event")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EventBridgeFifoTargets, "SqsParameters.MessageGroupId '$.detail.group' is used verbatim as the group id; AWS resolves the JSON path per event")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var sqs = ctx.Sqs;

        var fifoName = FidelityContext.UniqueName("sim2real-behav-q-") + ".fifo";
        var fifoUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = fifoName,
            Attributes = new() { ["FifoQueue"] = "true", ["ContentBasedDeduplication"] = "true" },
            Tags = new() { ["sim2real"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(fifoUrl));
        var fifoArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = fifoUrl, AttributeNames = ["QueueArn"] })).Attributes["QueueArn"];

        var busName = FidelityContext.UniqueName("sim2real-behav-bus-");
        await events.CreateEventBusAsync(new CreateEventBusRequest { Name = busName, Tags = [new Tag { Key = "sim2real", Value = Check }] });
        ctx.OnCleanup(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = busName }));

        var source = FidelityContext.UniqueName("sim2real.grouppath.");
        var ruleName = FidelityContext.UniqueName("sim2real-behav-rule-");
        await events.PutRuleAsync(new PutRuleRequest
        {
            Name = ruleName,
            EventBusName = busName,
            EventPattern = $$"""{"source": ["{{source}}"]}""",
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });

        ctx.OnCleanup(async () =>
        {
            var ids = ((await events.ListTargetsByRuleAsync(new ListTargetsByRuleRequest { Rule = ruleName, EventBusName = busName })).Targets
                ?? throw new KeyNotFoundException("'Targets'")).Select(t => t.Id).ToList();
            if (ids.Count > 0)
            {
                await events.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = ruleName, EventBusName = busName, Ids = ids });
            }

            await events.DeleteRuleAsync(new DeleteRuleRequest { Name = ruleName, EventBusName = busName });
        });
        var ruleArn = (await events.DescribeRuleAsync(new DescribeRuleRequest { Name = ruleName, EventBusName = busName })).Arn;

        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = fifoUrl,
            Attributes = new() { ["Policy"] = SqsPolicyForEvents(fifoArn, ruleArn).ToJsonString() },
        });

        var put = await events.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = ruleName,
            EventBusName = busName,
            Targets =
            [
                new Target
                {
                    Id = "grouped-target",
                    Arn = fifoArn,
                    SqsParameters = new SqsParameters { MessageGroupId = "$.detail.group" },
                },
            ],
        });
        Ensure(put.FailedEntryCount == 0, $"PutTargets failed: {put.FailedEntries?.Count} failed entries");

        // Two DIFFERENT group values prove the path is resolved per event, not pinned to one constant.
        var first = await PutUntilDelivered(ctx, busName, fifoUrl, source, "sim2real-group-alpha");
        Ensure(
            first == "sim2real-group-alpha",
            $"first message's MessageGroupId was '{first}', expected the event's own detail.group value 'sim2real-group-alpha'");

        var second = await PutUntilDelivered(ctx, busName, fifoUrl, source, "sim2real-group-beta");
        Ensure(
            second == "sim2real-group-beta",
            $"second message's MessageGroupId was '{second}', expected the event's own detail.group value 'sim2real-group-beta'");
    }

    private static JsonObject SqsPolicyForEvents(string queueArn, string ruleArn) => new()
    {
        ["Version"] = "2012-10-17",
        ["Statement"] = new JsonArray(new JsonObject
        {
            ["Sid"] = "AllowEventsSendMessage",
            ["Effect"] = "Allow",
            ["Principal"] = new JsonObject { ["Service"] = "events.amazonaws.com" },
            ["Action"] = "sqs:SendMessage",
            ["Resource"] = queueArn,
            ["Condition"] = new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = ruleArn } },
        }),
    };

    /// <summary>
    /// Poll the FIFO queue for a message carrying one of <c>event_ids</c>; return its MessageGroupId,
    /// or None once <c>timeout_s</c> passes.
    /// </summary>
    private static async Task<string?> ReceiveOne(FidelityContext ctx, string queueUrl, HashSet<string> eventIds, double timeoutS)
    {
        var sqs = ctx.Sqs;
        var started = FidelityTime.StartNew();
        while (started.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 5,
                MessageSystemAttributeNames = ["MessageGroupId"],
            });
            foreach (var msg in resp.Messages ?? [])
            {
                var body = JsonNode.Parse(msg.Body);
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                if (body?["id"] is JsonValue id && eventIds.Contains((string?)id ?? ""))
                {
                    return msg.Attributes["MessageGroupId"];
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Emit an event carrying <c>group_value</c> until one is delivered; return its MessageGroupId.
    ///
    /// A freshly-created rule or target can drop the first PutEvents silently, so a new event is sent
    /// every _RESEND_S until the overall timeout, capped by the runner's remaining time.
    /// </summary>
    private static async Task<string> PutUntilDelivered(FidelityContext ctx, string busName, string queueUrl, string source, string groupValue)
    {
        var events = ctx.Events;
        var timeoutS = BudgetS(ctx, DeliveryTimeoutS);
        var started = FidelityTime.StartNew();
        var eventIds = new HashSet<string>(StringComparer.Ordinal);
        while (started.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var resp = await events.PutEventsAsync(new PutEventsRequest
            {
                Entries =
                [
                    new PutEventsRequestEntry
                    {
                        EventBusName = busName,
                        Source = source,
                        DetailType = "sim2real.grouped",
                        Detail = $$"""{"group": "{{groupValue}}"}""",
                    },
                ],
            });
            Ensure(resp.FailedEntryCount == 0, $"PutEvents rejected the entry: {string.Join(", ", (resp.Entries ?? []).Select(e => $"{{'ErrorCode': '{e.ErrorCode}'}}"))}");
            eventIds.Add((resp.Entries ?? throw new KeyNotFoundException("'Entries'"))[0].EventId);
            var waitS = Math.Min(ResendS, Math.Max(1.0, timeoutS - started.Elapsed.TotalSeconds));
            var received = await ReceiveOne(ctx, queueUrl, eventIds, waitS);
            if (received is not null)
            {
                return received;
            }
        }

        throw new TimeoutException(
            $"no event carrying group '{groupValue}' reached the FIFO queue within {timeoutS.ToString("F0", CultureInfo.InvariantCulture)}s");
    }

    /// <summary>A wait's timeout, bounded by the runner's remaining time so the wait fails with its own message.</summary>
    private static double BudgetS(FidelityContext ctx, double capS)
    {
        var left = ctx.TimeLeftSeconds;
        return Math.Max(1.0, Math.Min(capS, left - 5.0));
    }
}

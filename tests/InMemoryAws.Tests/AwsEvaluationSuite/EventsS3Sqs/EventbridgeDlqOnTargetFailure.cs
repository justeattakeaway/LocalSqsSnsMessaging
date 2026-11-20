// Translated from behavioral/events__s3__sqs/check_eventbridge_dlq_on_target_failure.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.EventBridge.Model;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;
using SqsMessage = Amazon.SQS.Model.Message;

namespace InMemoryAws.Tests.AwsEvaluationSuite.EventsS3Sqs;

/// <summary>events__s3__sqs.eventbridge-dlq-on-target-failure</summary>
[Category("AwsEvaluationSuite")]
public sealed class EventbridgeDlqOnTargetFailure
{
    /// <summary>
    /// An event an unauthorized SQS target cannot receive lands in the target DLQ with ERROR_CODE
    /// NO_RESOURCE and the target ARN; once the queue policy is granted the same target receives the
    /// next event directly.
    /// </summary>
    [Test]
    [DisplayName("events__s3__sqs.eventbridge-dlq-on-target-failure")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EventBridgeUngrantedTargets, "Kept deliberately: a queue with no policy still receives EventBridge deliveries, so the event is delivered to the unauthorized target instead of dead-lettered as NO_RESOURCE")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var sqs = ctx.Sqs;
        var unauthorizedQueue = await CreateQueue(ctx); // no policy: delivery will fail
        var dlq = await CreateQueue(ctx);

        var busName = FidelityContext.UniqueName("bus-");
        await events.CreateEventBusAsync(new CreateEventBusRequest { Name = busName });
        ctx.OnCleanup(() => ctx.Events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = busName }));

        var ruleName = FidelityContext.UniqueName("rule-dlq-");
        await events.PutRuleAsync(new PutRuleRequest
        {
            Name = ruleName,
            EventBusName = busName,
            EventPattern = new JsonObject { ["source"] = new JsonArray("app.orders"), ["detail-type"] = new JsonArray("OrderPlaced") }.ToJsonString(),
        });
        ctx.OnCleanup(() => ctx.Events.DeleteRuleAsync(new DeleteRuleRequest { Name = ruleName, EventBusName = busName }));
        var ruleArn = await RuleArn(ctx, ruleName, busName);

        // DLQ needs a policy allowing events.amazonaws.com to send the failed event.
        await SetQueuePolicy(ctx, dlq.Url, SqsPolicyForEvents(dlq.Arn, ruleArn));

        await events.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = ruleName,
            EventBusName = busName,
            Targets =
            [
                new Target
                {
                    Id = "unauthorized-target",
                    Arn = unauthorizedQueue.Arn,
                    RetryPolicy = new RetryPolicy { MaximumRetryAttempts = 0, MaximumEventAgeInSeconds = 60 },
                    DeadLetterConfig = new DeadLetterConfig { Arn = dlq.Arn },
                },
            ],
        });
        ctx.OnCleanup(() => events.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = ruleName, Ids = ["unauthorized-target"], EventBusName = busName }));

        var dlqMessages = await ReemitUntilDelivered(ctx, dlq.Url, new PutEventsRequestEntry
        {
            Source = "app.orders",
            DetailType = "OrderPlaced",
            Detail = JsonSerializer.Serialize(new { order_id = "dlq-1" }),
            EventBusName = busName,
        });
        var dlqBody = dlqMessages[0].Body;
        Ensure(
            JsonNode.DeepEquals(dlqBody?["detail"], new JsonObject { ["order_id"] = "dlq-1" }),
            $"DLQ message does not carry the failed event's own detail: {dlqBody?.ToJsonString()}");
        var dlqAttrs = dlqMessages[0].Attributes;
        // SQS answers an unauthorized SendMessage as a missing queue, so EventBridge files NO_RESOURCE.
        // message varies: ERROR_MESSAGE embeds SQS's per-request Request ID.
        Ensure(
            dlqAttrs.GetValueOrDefault("ERROR_CODE")?.StringValue == "NO_RESOURCE",
            $"expected ERROR_CODE=NO_RESOURCE on the DLQ message, got {Describe(dlqAttrs)}");
        Ensure(
            dlqAttrs.GetValueOrDefault("TARGET_ARN")?.StringValue == unauthorizedQueue.Arn,
            $"expected TARGET_ARN={Py.Repr(unauthorizedQueue.Arn)} on the DLQ message, got {Describe(dlqAttrs)}");

        // Effect absent: the unauthorized target never received the event directly.
        var leaked = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = unauthorizedQueue.Url,
            MaxNumberOfMessages = 10,
            WaitTimeSeconds = 5,
        })).Messages ?? [];
        Ensure(
            leaked.Count == 0,
            $"unauthorized target received the event despite missing policy: [{string.Join(", ", leaked.Select(m => m.Body))}]");

        // Positive control: grant the missing policy and prove the identical rule/target now
        // delivers directly, so the DLQ capture above was genuinely failure-triggered and not a
        // target that unconditionally routes to its DeadLetterConfig.
        await SetQueuePolicy(ctx, unauthorizedQueue.Url, SqsPolicyForEvents(unauthorizedQueue.Arn, ruleArn));
        var directMessages = await ReemitUntilDelivered(ctx, unauthorizedQueue.Url, new PutEventsRequestEntry
        {
            Source = "app.orders",
            DetailType = "OrderPlaced",
            Detail = JsonSerializer.Serialize(new { order_id = "dlq-2-authorized" }),
            EventBusName = busName,
        });
        Ensure(
            JsonNode.DeepEquals(directMessages[0].Body?["detail"], new JsonObject { ["order_id"] = "dlq-2-authorized" }),
            $"authorized target did not receive the expected event directly: {directMessages[0].Body?.ToJsonString()}");
    }

    private static string Describe(Dictionary<string, MessageAttributeValue> attrs) =>
        "{" + string.Join(", ", attrs.Select(a => $"'{a.Key}': {{'StringValue': '{a.Value.StringValue}', 'DataType': '{a.Value.DataType}'}}")) + "}";

    private const string XsvcPrefix = "sim2real-xsvc-";
    private const string ManagedTagKey = "sim2real:managed";

    private sealed record Queue(string Name, string Url, string Arn);

    /// <summary>One received message: its body parsed as JSON (or the raw string), id, receipt handle and attributes.</summary>
    private sealed record Received(JsonNode? Body, string MessageId, string ReceiptHandle, Dictionary<string, MessageAttributeValue> Attributes);

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

    private static JsonObject SqsPolicyForEvents(string queueArn, string? ruleArn = null)
    {
        var statement = new JsonObject
        {
            ["Sid"] = "AllowEventsSendMessage",
            ["Effect"] = "Allow",
            ["Principal"] = new JsonObject { ["Service"] = "events.amazonaws.com" },
            ["Action"] = "sqs:SendMessage",
            ["Resource"] = queueArn,
        };
        if (ruleArn is not null)
        {
            statement["Condition"] = new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = ruleArn } };
        }

        return new JsonObject { ["Version"] = "2012-10-17", ["Statement"] = new JsonArray(statement) };
    }

    private static Task SetQueuePolicy(FidelityContext ctx, string queueUrl, JsonObject policy) =>
        ctx.Sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = queueUrl, Attributes = new() { ["Policy"] = policy.ToJsonString() } });

    private static async Task<string> RuleArn(FidelityContext ctx, string ruleName, string? busName = null) =>
        (await ctx.Events.DescribeRuleAsync(new DescribeRuleRequest { Name = ruleName, EventBusName = busName })).Arn;

    /// <summary>Poll SQS until expected_count messages arrive or timeout_s elapses; return what arrived.</summary>
    private static async Task<List<Received>> ReceiveMessages(FidelityContext ctx, string queueUrl, int expectedCount = 1, double timeoutS = 60.0, bool delete = true)
    {
        var sqs = ctx.Sqs;
        var messages = new List<Received>();
        var started = FidelityTime.StartNew();
        while (messages.Count < expectedCount && started.Elapsed.TotalSeconds < timeoutS)
        {
            var remaining = Math.Max(1, (int)(timeoutS - started.Elapsed.TotalSeconds));
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = Math.Min(10, expectedCount - messages.Count),
                WaitTimeSeconds = Math.Min(5, remaining),
                MessageAttributeNames = ["All"],
            });
            foreach (var msg in resp.Messages ?? [])
            {
                messages.Add(new Received(Body(msg), msg.MessageId, msg.ReceiptHandle, msg.MessageAttributes ?? []));
                if (delete)
                {
                    await sqs.DeleteMessageAsync(queueUrl, msg.ReceiptHandle);
                }
            }
        }

        return messages;
    }

    /// <summary>
    /// Emit an EventBridge event repeatedly until it lands on the SQS queue; return the messages list.
    /// A freshly-created rule/target can silently drop the first PutEvents before the path is live, and
    /// EventBridge never retries a dropped event, so re-emitting is more reliable than one longer wait.
    /// Raises AssertionError if never delivered.
    /// </summary>
    private static async Task<List<Received>> ReemitUntilDelivered(FidelityContext ctx, string queueUrl, PutEventsRequestEntry entry, int attempts = 8, double pollS = 6.0)
    {
        var events = ctx.Events;
        for (var i = 0; i < attempts; i++)
        {
            await events.PutEventsAsync(new PutEventsRequest { Entries = [entry] });
            var messages = await ReceiveMessages(ctx, queueUrl, expectedCount: 1, timeoutS: pollS);
            if (messages.Count > 0)
            {
                return messages;
            }
        }

        throw Fail($"event never delivered to {queueUrl} after {attempts} attempts of {pollS}s each");
    }
}

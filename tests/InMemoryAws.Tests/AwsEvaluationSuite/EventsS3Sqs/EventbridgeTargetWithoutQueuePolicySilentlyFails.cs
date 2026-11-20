// Translated from behavioral/events__s3__sqs/check_eventbridge_target_without_queue_policy_silently_fails.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.EventBridge.Model;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;
using SqsMessage = Amazon.SQS.Model.Message;

namespace InMemoryAws.Tests.AwsEvaluationSuite.EventsS3Sqs;

/// <summary>events__s3__sqs.eventbridge-target-without-queue-policy-silently-fails</summary>
[Category("AwsEvaluationSuite")]
public sealed class EventbridgeTargetWithoutQueuePolicySilentlyFails
{
    /// <summary>
    /// PutTargets accepts an SQS target without a queue policy but that queue never receives the
    /// event, while a policy-granted queue on the same rule receives it with its marker.
    /// </summary>
    [Test]
    [DisplayName("events__s3__sqs.eventbridge-target-without-queue-policy-silently-fails")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EventBridgeUngrantedTargets, "Kept deliberately: a queue with no policy still receives EventBridge deliveries, as most test setups add none; AWS refuses a queue that grants events.amazonaws.com nothing")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var queueNopolicy = await CreateQueue(ctx); // no policy allowing events.amazonaws.com

        var busName = FidelityContext.UniqueName("bus-");
        await events.CreateEventBusAsync(new CreateEventBusRequest { Name = busName });
        ctx.OnCleanup(() => ctx.Events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = busName }));

        var ruleName = FidelityContext.UniqueName("rule-nopolicy-");
        var ruleArn = (await events.PutRuleAsync(new PutRuleRequest
        {
            Name = ruleName,
            EventBusName = busName,
            EventPattern = new JsonObject { ["source"] = new JsonArray("app.orders"), ["detail-type"] = new JsonArray("OrderPlaced") }.ToJsonString(),
        })).RuleArn;
        ctx.OnCleanup(() => ctx.Events.DeleteRuleAsync(new DeleteRuleRequest { Name = ruleName, EventBusName = busName }));

        var queueWithpolicy = await CreateQueue(ctx);
        await SetQueuePolicy(ctx, queueWithpolicy.Url, SqsPolicyForEventBridge(queueWithpolicy.Arn, ruleArn));

        // PutTargets itself does not validate downstream permissions, it should succeed for both.
        await events.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = ruleName,
            EventBusName = busName,
            Targets =
            [
                new Target { Id = "sqs-target-nopolicy", Arn = queueNopolicy.Arn },
                new Target { Id = "sqs-target-withpolicy", Arn = queueWithpolicy.Arn },
            ],
        });
        ctx.OnCleanup(() => events.RemoveTargetsAsync(new RemoveTargetsRequest
        {
            Rule = ruleName,
            Ids = ["sqs-target-nopolicy", "sqs-target-withpolicy"],
            EventBusName = busName,
        }));

        var marker = FidelityContext.UniqueName("marker-");
        var sendTime = FidelityTime.StartNew();
        await events.PutEventsAsync(new PutEventsRequest
        {
            Entries =
            [
                new PutEventsRequestEntry
                {
                    Source = "app.orders",
                    DetailType = "OrderPlaced",
                    Detail = JsonSerializer.Serialize(new { order_id = "unauthorized", marker }),
                    EventBusName = busName,
                },
            ],
        });

        var delivered = await PollForMarker(ctx, queueWithpolicy.Url, marker, timeoutS: 60.0);
        Ensure(
            (string?)delivered["detail"]?["marker"] == marker,
            "positive-control queue (WITH the resource policy) never received the event -- cannot attribute the no-policy queue's silence to policy enforcement rather than a delivery path that never worked in this run");
        var positiveLatencyS = sendTime.Elapsed.TotalSeconds;
        var holdS = Math.Max(15.0, positiveLatencyS * 3);
        var messages = await PollForNoMessages(ctx, queueNopolicy.Url, windowS: holdS);
        Ensure(
            messages.Count == 0,
            "queue received a message despite missing resource policy for events.amazonaws.com -- emulator must not fabricate delivery that AWS would reject");
    }

    /// <summary>
    /// True for S3's one-off destination-validation handshake message. When a queue is first attached
    /// as an S3 notification destination, S3 posts {"Service":"Amazon S3","Event":"s3:TestEvent", ...}
    /// to prove it can write to the queue. It carries no "Records" and is NOT an object event, so it
    /// must never be mistaken for one (nor counted as an over-delivery).
    /// </summary>
    private static bool IsS3TestEvent(JsonNode? body) => body is JsonObject o && (string?)o["Event"] == "s3:TestEvent";

    /// <summary>
    /// Watch a queue for *window_s* and return whatever arrived (expected: nothing). Exits early as
    /// soon as any real message appears so a leak fails fast.
    /// </summary>
    private static async Task<List<JsonNode?>> PollForNoMessages(FidelityContext ctx, string queueUrl, double windowS = 45, bool ignoreS3TestEvent = true)
    {
        var sqs = ctx.Sqs;
        var started = FidelityTime.StartNew();
        var received = new List<JsonNode?>();
        while (started.Elapsed.TotalSeconds < windowS)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 5,
                MessageAttributeNames = ["All"],
            });
            foreach (var m in resp.Messages ?? [])
            {
                var body = Body(m);
                if (ignoreS3TestEvent && IsS3TestEvent(body))
                {
                    await sqs.DeleteMessageAsync(queueUrl, m.ReceiptHandle);
                    continue;
                }

                received.Add(body);
            }

            if (received.Count > 0)
            {
                break;
            }
        }

        return received;
    }

    private static JsonObject SqsPolicyForEventBridge(string queueArn, string ruleArn) => new()
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

    /// <summary>Poll until a message whose detail.marker == marker arrives; raise on timeout.</summary>
    private static async Task<JsonObject> PollForMarker(FidelityContext ctx, string queueUrl, string marker, double timeoutS = 60.0)
    {
        var sqs = ctx.Sqs;
        var started = FidelityTime.StartNew();
        while (started.Elapsed.TotalSeconds < timeoutS)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
            foreach (var msg in resp.Messages ?? [])
            {
                var body = JsonNode.Parse(msg.Body);
                await sqs.DeleteMessageAsync(queueUrl, msg.ReceiptHandle);
                if (body is JsonObject o && o["detail"] is JsonObject d && (string?)d["marker"] == marker)
                {
                    return o;
                }
            }
        }

        throw Fail($"event with marker {marker} was never delivered to {queueUrl} within {timeoutS}s");
    }

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

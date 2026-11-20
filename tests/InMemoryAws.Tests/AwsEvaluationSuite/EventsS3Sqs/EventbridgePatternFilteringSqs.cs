// Translated from behavioral/events__s3__sqs/check_eventbridge_pattern_filtering_sqs.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.EventBridge.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;
using SqsMessage = Amazon.SQS.Model.Message;

namespace InMemoryAws.Tests.AwsEvaluationSuite.EventsS3Sqs;

/// <summary>events__s3__sqs.eventbridge-pattern-filtering-sqs</summary>
[Category("AwsEvaluationSuite")]
public sealed class EventbridgePatternFilteringSqs
{
    /// <summary>EventBridge rule pattern matching only forwards matching detail-types to SQS.</summary>
    [Test]
    [DisplayName("events__s3__sqs.eventbridge-pattern-filtering-sqs")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var queue = await CreateQueue(ctx);

        var busName = FidelityContext.UniqueName("bus-");
        await events.CreateEventBusAsync(new CreateEventBusRequest { Name = busName });
        ctx.OnCleanup(() => ctx.Events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = busName }));

        var ruleName = FidelityContext.UniqueName("rule-filter-");
        await events.PutRuleAsync(new PutRuleRequest
        {
            Name = ruleName,
            EventBusName = busName,
            EventPattern = new JsonObject { ["source"] = new JsonArray("app.orders"), ["detail-type"] = new JsonArray("OrderShipped") }.ToJsonString(),
        });
        ctx.OnCleanup(() => ctx.Events.DeleteRuleAsync(new DeleteRuleRequest { Name = ruleName, EventBusName = busName }));
        var ruleArn = await RuleArn(ctx, ruleName, busName);
        await SetQueuePolicy(ctx, queue.Url, SqsPolicyForEvents(queue.Arn, ruleArn));

        await events.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = ruleName,
            EventBusName = busName,
            Targets = [new Target { Id = "sqs-target", Arn = queue.Arn }],
        });
        ctx.OnCleanup(() => events.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = ruleName, Ids = ["sqs-target"], EventBusName = busName }));

        await events.PutEventsAsync(new PutEventsRequest
        {
            Entries =
            [
                new PutEventsRequestEntry
                {
                    Source = "app.orders",
                    DetailType = "OrderPlaced", // does NOT match pattern
                    Detail = JsonSerializer.Serialize(new { order_id = "no-match" }),
                    EventBusName = busName,
                },
                new PutEventsRequestEntry
                {
                    Source = "app.orders",
                    DetailType = "OrderShipped", // matches pattern
                    Detail = JsonSerializer.Serialize(new { order_id = "match-1" }),
                    EventBusName = busName,
                },
            ],
        });

        // Drain to quiet rather than stopping at the first message received: a fixed-count wait
        // returns as soon as ANY one message shows up, so a target that (incorrectly) forwarded
        // BOTH entries could still pass whenever the matching one happened to be received first --
        // the non-matching entry would sit unread in the queue and never be checked for absence.
        var messages = await DrainToQuiet(ctx, queue.Url, quietS: 15.0, maxTotalS: 90.0);
        Ensure(
            messages.Count == 1,
            $"expected exactly 1 filtered message, got {messages.Count}: [{string.Join(", ", messages.Select(m => m.Body?.ToJsonString()))}]");
        var detail = messages[0].Body;
        Ensure(
            (string?)detail?["detail"]?["order_id"] == "match-1",
            $"expected order_id 'match-1', got {Py.Repr((string?)detail?["detail"]?["order_id"])}");
        Ensure(
            (string?)detail?["detail-type"] == "OrderShipped",
            $"expected detail-type 'OrderShipped', got {Py.Repr((string?)detail?["detail-type"])}");

        var depth = await WaitCountersZero(ctx, ctx.Sqs, queue.Url, ["ApproximateNumberOfMessages", "ApproximateNumberOfMessagesNotVisible"]);
        Ensure(
            depth.GetValueOrDefault("ApproximateNumberOfMessages") == "0"
            && depth.GetValueOrDefault("ApproximateNumberOfMessagesNotVisible") == "0"
            && depth.Count == 2,
            $"queue still reports {{{string.Join(", ", depth.Select(d => $"'{d.Key}': '{d.Value}'"))}}} after drain -- the non-matching entry may still be in flight");
    }

    /// <summary>A poll's timeout: cap_s, bounded by the runner's remaining time less 5 s.</summary>
    private static double BudgetS(FidelityContext ctx, double capS) => Math.Max(1.0, Math.Min(capS, ctx.TimeLeftSeconds - 5.0));

    /// <summary>
    /// Read the queue's *names* counters until each is "0" or cap_s ends; return the last read.
    /// SQS documents these counters as eventually consistent: they "may not achieve consistency until
    /// at least 1 minute after the producers stop sending messages" (GetQueueAttributes).
    /// </summary>
    private static async Task<Dictionary<string, string>> WaitCountersZero(FidelityContext ctx, AmazonSQSClient sqs, string queueUrl, List<string> names, double capS = 90.0, double intervalS = 3.0)
    {
        var start = FidelityTime.StartNew();
        var budgetS = BudgetS(ctx, capS);
        var stop = Math.Min(capS, budgetS);
        while (true)
        {
            var attrs = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = queueUrl, AttributeNames = names })).Attributes;
            if (names.All(n => attrs.GetValueOrDefault(n) == "0"))
            {
                return attrs;
            }

            if (start.Elapsed.TotalSeconds >= stop)
            {
                if (budgetS < capS)
                {
                    throw new TimeoutException(
                        $"the check's time budget ran out after {start.Elapsed.TotalSeconds:F1}s, before the {capS}s counter wait ended; not graded");
                }

                return attrs;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }
    }

    /// <summary>
    /// Poll SQS until `quiet_s` elapses with no new message, or `max_total_s` is exhausted. Unlike a
    /// fixed-count wait, this never stops the instant a message shows up -- it keeps draining so a
    /// second, wrongly-delivered message is caught rather than left unread.
    /// </summary>
    private static async Task<List<Received>> DrainToQuiet(FidelityContext ctx, string queueUrl, double quietS = 15.0, double maxTotalS = 90.0, bool delete = true)
    {
        var sqs = ctx.Sqs;
        var messages = new List<Received>();
        var clock = FidelityTime.StartNew();
        var lastSeen = clock.Elapsed;
        while (clock.Elapsed.TotalSeconds < maxTotalS && (clock.Elapsed - lastSeen).TotalSeconds < quietS)
        {
            var remaining = Math.Max(1, (int)(maxTotalS - clock.Elapsed.TotalSeconds));
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = Math.Min(5, remaining),
                MessageAttributeNames = ["All"],
            });
            var batch = resp.Messages ?? [];
            if (batch.Count > 0)
            {
                lastSeen = clock.Elapsed;
            }

            foreach (var msg in batch)
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

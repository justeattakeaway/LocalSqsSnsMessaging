// Translated from behavioral/events__s3__sqs/check_eventbridge_fifo_queue_target_arn.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.EventBridge.Model;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;
using SqsMessage = Amazon.SQS.Model.Message;
using Tag = Amazon.EventBridge.Model.Tag;

namespace InMemoryAws.Tests.AwsEvaluationSuite.EventsS3Sqs;

/// <summary>events__s3__sqs.eventbridge-fifo-queue-target-arn</summary>
[Category("AwsEvaluationSuite")]
public sealed class EventbridgeFifoQueueTargetArn
{
    private const string Check = "eventbridge-fifo-queue-target-arn";

    /// <summary>
    /// A FIFO queue target delivers with SqsParameters.MessageGroupId, and the same target without it
    /// is refused with ValidationException and its exact message, leaving no target registered.
    /// </summary>
    [Test]
    [DisplayName("events__s3__sqs.eventbridge-fifo-queue-target-arn")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var events = ctx.Events;
        var fifoName = FidelityContext.UniqueName("fifo-q-") + ".fifo";
        var fifo = await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = fifoName,
            Attributes = new() { ["FifoQueue"] = "true", ["ContentBasedDeduplication"] = "true" },
            Tags = new() { ["sim2real-check"] = Check },
        });
        var fifoUrl = fifo.QueueUrl;
        ctx.OnCleanup(() => ctx.Sqs.DeleteQueueAsync(fifoUrl));
        var fifoArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = fifoUrl, AttributeNames = ["QueueArn"] })).Attributes["QueueArn"];

        var busName = FidelityContext.UniqueName("bus-");
        await events.CreateEventBusAsync(new CreateEventBusRequest { Name = busName, Tags = [new Tag { Key = "sim2real-check", Value = Check }] });
        ctx.OnCleanup(() => ctx.Events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = busName }));

        var ruleName = FidelityContext.UniqueName("rule-fifo-");
        await events.PutRuleAsync(new PutRuleRequest
        {
            Name = ruleName,
            EventBusName = busName,
            EventPattern = new JsonObject { ["source"] = new JsonArray("app.orders"), ["detail-type"] = new JsonArray("OrderPlaced") }.ToJsonString(),
        });
        ctx.OnCleanup(() => ctx.Events.DeleteRuleAsync(new DeleteRuleRequest { Name = ruleName, EventBusName = busName }));
        var ruleArn = await RuleArn(ctx, ruleName, busName);
        await SetQueuePolicy(ctx, fifoUrl, SqsPolicyForEvents(fifoArn, ruleArn));

        // Positive control: with MessageGroupId present, the target Arn resolves and the event is
        // actually delivered -- proof the wiring (policy, rule, bus) works before anything is broken.
        await events.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = ruleName,
            EventBusName = busName,
            Targets = [new Target { Id = "fifo-target", Arn = fifoArn, SqsParameters = new SqsParameters { MessageGroupId = "orders-group" } }],
        });
        ctx.OnCleanup(() => events.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = ruleName, Ids = ["fifo-target"], EventBusName = busName }));

        // Re-emit until delivered: a fresh rule can drop the first PutEvents; _reemit raises if the
        // event never lands on the FIFO target.
        await ReemitUntilDelivered(ctx, fifoUrl, new PutEventsRequestEntry
        {
            Source = "app.orders",
            DetailType = "OrderPlaced",
            Detail = JsonSerializer.Serialize(new { order_id = "fifo-1" }),
            EventBusName = busName,
        });

        // Break exactly one thing: remove the working target, then re-add the identical Arn without
        // SqsParameters.MessageGroupId.
        await events.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = ruleName, Ids = ["fifo-target"], EventBusName = busName });
        var (err, message) = await Error(() => events.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = ruleName,
            EventBusName = busName,
            Targets = [new Target { Id = "fifo-target", Arn = fifoArn }],
        }));
        Ensure(
            err == "ValidationException",
            $"put_targets against a FIFO queue without SqsParameters.MessageGroupId should fail with ValidationException, got {Py.Repr(err)}");
        Ensure(
            message == "Parameter(s) SqsParameters must be specified for target: fifo-target.",
            $"unexpected ValidationException message: {Py.Repr(message)}");

        // Effect absent, via an independent read: the rejected call registered nothing.
        var remaining = (await events.ListTargetsByRuleAsync(new ListTargetsByRuleRequest { Rule = ruleName, EventBusName = busName })).Targets ?? [];
        Ensure(
            remaining.Count == 0,
            $"put_targets was rejected but a target is still registered: [{string.Join(", ", remaining.Select(t => t.Id))}]");
    }

    /// <summary>
    /// Call fn(); return the AWS error (Code, Message) if it raised a ClientError. An unexpected
    /// SUCCESS raises, because an emulator ACCEPTING input real AWS refuses is the strongest signal
    /// available.
    /// </summary>
    private static async Task<(string Code, string Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code ?? string.Empty, error.Message ?? string.Empty);
        }

        throw Fail($"expected put_targets to be refused but it SUCCEEDED and returned {result}");
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

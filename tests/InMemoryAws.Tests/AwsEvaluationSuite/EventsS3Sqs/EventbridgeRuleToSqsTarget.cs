// Translated from behavioral/events__s3__sqs/check_eventbridge_rule_to_sqs_target.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;
using SqsMessage = Amazon.SQS.Model.Message;
using Tag = Amazon.EventBridge.Model.Tag;

namespace InMemoryAws.Tests.AwsEvaluationSuite.EventsS3Sqs;

/// <summary>events__s3__sqs.eventbridge-rule-to-sqs-target</summary>
[Category("AwsEvaluationSuite")]
public sealed class EventbridgeRuleToSqsTarget
{
    private const string Check = "eventbridge-rule-to-sqs-target";

    /// <summary>
    /// A custom EventBridge rule delivers matching put_events entries to SQS; the target registration
    /// is independently visible via list_targets_by_rule before delivery; and once the
    /// target/rule/bus are deleted, an independent read at each shows it gone.
    /// </summary>
    [Test]
    [DisplayName("events__s3__sqs.eventbridge-rule-to-sqs-target")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var queue = await CreateQueue(ctx);

        var busName = FidelityContext.UniqueName("bus-");
        await events.CreateEventBusAsync(new CreateEventBusRequest { Name = busName, Tags = [new Tag { Key = "sim2real-check", Value = Check }] });
        ctx.OnCleanup(() => ctx.Events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = busName }));

        var ruleName = FidelityContext.UniqueName("rule-");
        await events.PutRuleAsync(new PutRuleRequest
        {
            Name = ruleName,
            EventBusName = busName,
            EventPattern = new JsonObject { ["source"] = new JsonArray("app.orders"), ["detail-type"] = new JsonArray("OrderPlaced") }.ToJsonString(),
            Tags = [new Tag { Key = "sim2real-check", Value = Check }],
        });
        ctx.OnCleanup(() => UnlessGone(() => ctx.Events.DeleteRuleAsync(new DeleteRuleRequest { Name = ruleName, EventBusName = busName })));
        var ruleArn = await RuleArn(ctx, ruleName, busName);
        await SetQueuePolicy(ctx, queue.Url, SqsPolicyForEvents(queue.Arn, ruleArn));

        await events.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = ruleName,
            EventBusName = busName,
            Targets = [new Target { Id = "sqs-target", Arn = queue.Arn }],
        });
        ctx.OnCleanup(() => UnlessGone(() => events.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = ruleName, Ids = ["sqs-target"], EventBusName = busName })));

        var registered = (await events.ListTargetsByRuleAsync(new ListTargetsByRuleRequest { Rule = ruleName, EventBusName = busName })).Targets ?? [];
        Ensure(
            registered.Select(t => t.Arn).SequenceEqual([queue.Arn]),
            $"expected the queue registered as the rule's only target, got [{string.Join(", ", registered.Select(t => t.Arn))}]");

        var messages = await ReemitUntilDelivered(ctx, queue.Url, new PutEventsRequestEntry
        {
            Source = "app.orders",
            DetailType = "OrderPlaced",
            Detail = JsonSerializer.Serialize(new { order_id = "abc123" }),
            EventBusName = busName,
        });
        var detail = messages[0].Body;
        Ensure(
            (string?)detail?["detail"]?["order_id"] == "abc123",
            $"expected delivered detail.order_id == 'abc123', got {detail?.ToJsonString()}");

        await events.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = ruleName, Ids = ["sqs-target"], EventBusName = busName });
        var afterRemove = (await events.ListTargetsByRuleAsync(new ListTargetsByRuleRequest { Rule = ruleName, EventBusName = busName })).Targets ?? [];
        Ensure(afterRemove.Count == 0, $"expected no targets after remove_targets, got [{string.Join(", ", afterRemove.Select(t => t.Id))}]");

        await events.DeleteRuleAsync(new DeleteRuleRequest { Name = ruleName, EventBusName = busName });
        var (code, message) = await Error(() => events.DescribeRuleAsync(new DescribeRuleRequest { Name = ruleName, EventBusName = busName }));
        Ensure(
            code == "ResourceNotFoundException",
            $"expected describe_rule on a deleted rule to fail ResourceNotFoundException, got {Py.Repr(code)}");
        Ensure(
            message == $"Rule {ruleName} does not exist on EventBus {busName}.",
            $"unexpected describe_rule message: {Py.Repr(message)}");

        await events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = busName });
        (code, message) = await Error(() => events.DescribeEventBusAsync(new DescribeEventBusRequest { Name = busName }));
        Ensure(
            code == "ResourceNotFoundException",
            $"expected describe_event_bus on a deleted bus to fail ResourceNotFoundException, got {Py.Repr(code)}");
        Ensure(
            message == $"Event bus {busName} does not exist.",
            $"unexpected describe_event_bus message: {Py.Repr(message)}");
    }

    /// <summary>Run fn; return the ClientError (Code, Message). Raises if fn unexpectedly succeeds.</summary>
    private static async Task<(string Code, string Message)> Error(Func<Task> fn)
    {
        if (await FidelityContext.CaptureError(fn) is { } err)
        {
            return (err.Code ?? string.Empty, err.Message ?? string.Empty);
        }

        throw Fail("expected a ClientError, call unexpectedly succeeded");
    }

    /// <summary>Teardown for a resource the check deletes itself: a ResourceNotFoundException is done.</summary>
    private static async Task UnlessGone(Func<Task> fn)
    {
        try
        {
            await fn();
        }
        catch (AmazonEventBridgeException exc) when (exc.ErrorCode == "ResourceNotFoundException")
        {
        }
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

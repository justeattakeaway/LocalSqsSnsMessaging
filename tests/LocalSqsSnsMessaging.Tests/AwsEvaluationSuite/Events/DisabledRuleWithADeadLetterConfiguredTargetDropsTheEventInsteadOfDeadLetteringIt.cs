// Translated from behavioral/events/check_disabled_rule_with_a_dead_letter_configured_target_drops_the_event_instead_of_dead_lettering_it.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Amazon.EventBridge.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.disabled-rule-with-a-dead-letter-configured-target-drops-the-event-instead-of-dead-lettering-it</summary>
[Category("AwsEvaluationSuite")]
public sealed partial class DisabledRuleWithADeadLetterConfiguredTargetDropsTheEventInsteadOfDeadLetteringIt
{
    private const string Check = "disabled-rule-with-a-dead-letter-configured-target-drops-the-event-instead-of-dead-lettering-it";
    private const double SilenceWindowS = 60.0;
    private const double DeliveryTimeoutS = 150.0;

    /// <summary>
    /// While a rule is DISABLED, an event matching it never reaches its target's dead-letter queue; once
    /// ENABLED, the next matching event is dead-lettered with ERROR_CODE NO_PERMISSIONS, SQS's exact
    /// AccessDenied message and the rule's ARN, and the disabled-period event never appears.
    /// </summary>
    [Test]
    [DisplayName("events.disabled-rule-with-a-dead-letter-configured-target-drops-the-event-instead-of-dead-lettering-it")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EventBridgeUngrantedTargets, "Kept deliberately: a queue with no policy still receives EventBridge deliveries, so the enabled rule's event is delivered instead of dead-lettered as NO_PERMISSIONS")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var sqs = ctx.Sqs;

        var bus = FidelityContext.UniqueName("sim2real-evb-disdlq-");
        await events.CreateEventBusAsync(new CreateEventBusRequest { Name = bus, Tags = [new Tag { Key = "sim2real", Value = Check }] });
        ctx.OnCleanup(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));
        var source = FidelityContext.UniqueName("sim2real.disdlq.");
        var rule = FidelityContext.UniqueName("sim2real-evb-disdlqrule-");
        var ruleArn = (await events.PutRuleAsync(new PutRuleRequest
        {
            Name = rule, EventBusName = bus, EventPattern = $$"""{"source": ["{{source}}"]}""",
        })).RuleArn;
        ctx.OnCleanup(() => events.DeleteRuleAsync(new DeleteRuleRequest { Name = rule, EventBusName = bus }));

        var (bareUrl, bareArn) = await Queue(ctx, sqs, "bare");
        var (dlqUrl, dlqArn) = await Queue(ctx, sqs, "dlq");
        await SetPolicy(sqs, dlqUrl, dlqArn, ruleArn);

        var retry = new RetryPolicy { MaximumRetryAttempts = 185, MaximumEventAgeInSeconds = 86400 };
        var put = await events.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = rule,
            EventBusName = bus,
            Targets = [new Target { Id = "bare", Arn = bareArn, RetryPolicy = retry, DeadLetterConfig = new DeadLetterConfig { Arn = dlqArn } }],
        });
        ctx.OnCleanup(() => events.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = rule, EventBusName = bus, Ids = ["bare"] }));
        Ensure(put.FailedEntryCount == 0, $"PutTargets reported {put.FailedEntries}");

        await events.DisableRuleAsync(new DisableRuleRequest { Name = rule, EventBusName = bus });
        var described = await events.DescribeRuleAsync(new DescribeRuleRequest { Name = rule, EventBusName = bus });
        Ensure(described.State?.Value == "DISABLED", $"the rule's State after DisableRule was '{described.State?.Value}'");

        var silentId = await Put(events, bus, source, FidelityContext.UniqueName("silent-"));
        var seenWhileDisabled = await DrainFor(sqs, dlqUrl, SilenceWindowS);
        Ensure(
            !seenWhileDisabled.Any(m => (string?)m.Body["id"] == silentId),
            $"a dead letter for the event sent while DISABLED arrived: {Repr(seenWhileDisabled)}");
        Ensure(
            seenWhileDisabled.Count == 0,
            $"the DLQ received {seenWhileDisabled.Count} message(s) while the rule was disabled, expected none");

        await events.EnableRuleAsync(new EnableRuleRequest { Name = rule, EventBusName = bus });
        described = await events.DescribeRuleAsync(new DescribeRuleRequest { Name = rule, EventBusName = bus });
        Ensure(described.State?.Value == "ENABLED", $"the rule's State after EnableRule was '{described.State?.Value}'");

        var liveId = await Put(events, bus, source, FidelityContext.UniqueName("live-"));
        var passedOver = new List<(JsonNode Body, Dictionary<string, string?> Attrs)>();
        var (_, attrs) = await Await(
            sqs, dlqUrl, (b, a) => (string?)b["id"] == liveId, DeliveryTimeoutS, passedOver);
        Ensure(
            attrs["ERROR_CODE"] == "NO_PERMISSIONS",
            $"the always-failing target dead-lettered the post-enable event as '{attrs["ERROR_CODE"]}'");
        var expectedMessage =
            "User: events.amazonaws.com is not authorized to perform: sqs:sendmessage on resource: " +
            $"{bareArn} because no resource-based policy allows the sqs:sendmessage action " +
            "(Service: AmazonSQS; Status Code: 403; Error Code: AccessDenied; Request ID: <id>; Proxy: null)";
        Ensure(
            Shaped(attrs["ERROR_MESSAGE"]) == expectedMessage,
            $"the post-enable target's ERROR_MESSAGE was '{Shaped(attrs["ERROR_MESSAGE"])}'");
        Ensure(attrs["RULE_ARN"] == ruleArn, "the post-enable dead-letter RULE_ARN does not name the rule");

        var leftover = passedOver.Concat(await DrainFor(sqs, dlqUrl, 20.0)).ToList();
        Ensure(
            !leftover.Any(m => (string?)m.Body["id"] == silentId),
            "the event sent while the rule was disabled arrived in the DLQ after a delay -- it was " +
            "evaluated late rather than never evaluated");
    }

    [GeneratedRegex("Request ID: [^;]+;")]
    private static partial Regex RequestIdPattern();

    private static string Shaped(string? message) => RequestIdPattern().Replace(message ?? "", "Request ID: <id>;");

    private static string Repr(List<(JsonNode Body, Dictionary<string, string?> Attrs)> messages) =>
        "[" + string.Join(", ", messages.Select(m => $"({m.Body.ToJsonString()}, {{{string.Join(", ", m.Attrs.Select(kv => $"'{kv.Key}': '{kv.Value}'"))}}})")) + "]";

    private static async Task<(string Url, string Arn)> Queue(FidelityContext ctx, IAmazonSQS sqs, string label)
    {
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName($"sim2real-evb-disdlq-{label}-"),
            Tags = new() { ["sim2real"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes["QueueArn"];
        return (url, arn);
    }

    private static Task SetPolicy(IAmazonSQS sqs, string url, string arn, string ruleArn) =>
        sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = url,
            Attributes = new()
            {
                ["Policy"] = new JsonObject
                {
                    ["Version"] = "2012-10-17",
                    ["Statement"] = new JsonArray(new JsonObject
                    {
                        ["Effect"] = "Allow",
                        ["Principal"] = new JsonObject { ["Service"] = "events.amazonaws.com" },
                        ["Action"] = "sqs:SendMessage",
                        ["Resource"] = arn,
                        ["Condition"] = new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = ruleArn } },
                    }),
                }.ToJsonString(),
            },
        });

    private static async Task<string> Put(Amazon.EventBridge.IAmazonEventBridge events, string bus, string source, string marker)
    {
        var resp = await events.PutEventsAsync(new PutEventsRequest
        {
            Entries =
            [
                new PutEventsRequestEntry
                {
                    EventBusName = bus, Source = source, DetailType = "sim2real-disdlq",
                    Detail = $$"""{"marker": "{{marker}}"}""",
                },
            ],
        });
        Ensure(resp.FailedEntryCount == 0, $"PutEvents rejected the entry: {resp.Entries}");
        return resp.Entries[0].EventId;
    }

    private static async Task<List<(JsonNode Body, Dictionary<string, string?> Attrs)>> Receive(IAmazonSQS sqs, string url)
    {
        var output = new List<(JsonNode Body, Dictionary<string, string?> Attrs)>();
        var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 5, MessageAttributeNames = ["All"],
        });
        foreach (var m in resp.Messages ?? [])
        {
            await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle });
            var body = JsonNode.Parse(m.Body)!;
            var attrs = (m.MessageAttributes ?? []).ToDictionary(kv => kv.Key, kv => (string?)kv.Value.StringValue, StringComparer.Ordinal);
            output.Add((body, attrs));
        }

        return output;
    }

    private static async Task<List<(JsonNode Body, Dictionary<string, string?> Attrs)>> DrainFor(IAmazonSQS sqs, string url, double windowS)
    {
        var output = new List<(JsonNode Body, Dictionary<string, string?> Attrs)>();
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(windowS))
        {
            output.AddRange(await Receive(sqs, url));
        }

        return output;
    }

    /// <summary>The first matching message; every other message received meanwhile goes to `passed_over`.</summary>
    private static async Task<(JsonNode Body, Dictionary<string, string?> Attrs)> Await(
        IAmazonSQS sqs, string url, Func<JsonNode, Dictionary<string, string?>, bool> match, double timeoutS,
        List<(JsonNode Body, Dictionary<string, string?> Attrs)> passedOver)
    {
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            foreach (var (body, attrs) in await Receive(sqs, url))
            {
                if (match(body, attrs))
                {
                    return (body, attrs);
                }

                passedOver.Add((body, attrs));
            }
        }

        throw Fail($"no matching message reached the queue within {timeoutS:0.0}s");
    }
}

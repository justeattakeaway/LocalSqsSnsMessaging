// Translated from behavioral/events/check_sns_target_without_topic_policy_dead_letters_as_access_denied_until_the_policy_grants_publish.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.sns-target-without-topic-policy-dead-letters-as-access-denied-until-the-policy-grants-publish</summary>
[Category("AwsEvaluationSuite")]
public sealed partial class SnsTargetWithoutTopicPolicyDeadLettersAsAccessDeniedUntilThePolicyGrantsPublish
{
    private const string Check = "sns-target-without-topic-policy-dead-letters-as-access-denied-until-the-policy-grants-publish";
    private static readonly string[] DeadLetterKeys = ["ERROR_CODE", "ERROR_MESSAGE", "RULE_ARN", "TARGET_ARN"];
    private const double DeliveryTimeoutS = 180.0;

    // SNS's own AccessDenied as EventBridge records it; the minted request id is shaped.
    private const string AccessDenied =
        "User: <id> is not authorized to perform: SNS:Publish on resource: " +
        "<id> because no resource-based policy allows the SNS:Publish action (Service: AmazonSNS; " +
        "Status Code: 403; Error Code: AuthorizationError; Request ID: <id>; Proxy: null)";

    /// <summary>
    /// An SNS target whose topic policy lacks a Publish grant for events.amazonaws.com is dead-lettered as
    /// NO_PERMISSIONS with SNS's AccessDenied message, rule and topic ARNs, while a healthy sibling receives the
    /// event; adding the Publish statement lets the next event reach the topic's subscribed queue.
    /// </summary>
    [Test]
    [DisplayName("events.sns-target-without-topic-policy-dead-letters-as-access-denied-until-the-policy-grants-publish")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EventBridgeSnsTargets, "an SNS topic target is never published to nor dead-lettered (only SQS targets are delivered); AWS dead-letters it as NO_PERMISSIONS when the topic policy grants no Publish")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var sns = ctx.Sns;
        var sqs = ctx.Sqs;

        var bus = FidelityContext.UniqueName("sim2real-evb-snsdlq-");
        await events.CreateEventBusAsync(new CreateEventBusRequest { Name = bus, Tags = [new Amazon.EventBridge.Model.Tag { Key = "sim2real", Value = Check }] });
        ctx.OnCleanup(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));
        var source = FidelityContext.UniqueName("sim2real.snsdlq.");
        var rule = FidelityContext.UniqueName("sim2real-evb-snsdlqrule-");
        var ruleArn = (await events.PutRuleAsync(new PutRuleRequest
        {
            Name = rule,
            EventBusName = bus,
            EventPattern = $$"""{"source": ["{{source}}"]}""",
        })).RuleArn;
        ctx.OnCleanup(() => events.DeleteRuleAsync(new DeleteRuleRequest { Name = rule, EventBusName = bus }));

        var topicArn = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = FidelityContext.UniqueName("sim2real-evb-snsdlq-topic-"),
            Tags = [new Amazon.SimpleNotificationService.Model.Tag { Key = "sim2real", Value = Check }],
        })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = topicArn }));

        var (healthyUrl, healthyArn) = await Queue(ctx, sqs, "ok");
        var (dlqUrl, dlqArn) = await Queue(ctx, sqs, "dlq");
        await SetSqsPolicy(sqs, healthyUrl, healthyArn, ruleArn, "Allow");
        await SetSqsPolicy(sqs, dlqUrl, dlqArn, ruleArn, "Allow");

        var retry = new RetryPolicy { MaximumRetryAttempts = 180, MaximumEventAgeInSeconds = 86400 };
        var resp = await events.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = rule,
            EventBusName = bus,
            Targets =
            [
                new Target { Id = "denied", Arn = topicArn, RetryPolicy = retry, DeadLetterConfig = new DeadLetterConfig { Arn = dlqArn } },
                new Target { Id = "healthy", Arn = healthyArn },
            ],
        });
        ctx.OnCleanup(() => events.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = rule, EventBusName = bus, Ids = ["denied", "healthy"] }));
        Ensure(resp.FailedEntryCount == 0, $"PutTargets reported {resp.FailedEntries?.Count} failed entries");

        var eventId = await Put(events, bus, source, FidelityContext.UniqueName("m-"));
        var (healthy, _) = await AwaitSqs(sqs, healthyUrl, (b, _) => (string?)b?["id"] == eventId, DeliveryTimeoutS);

        var (body, attrs) = await AwaitSqs(sqs, dlqUrl, (b, _) => (string?)b?["id"] == eventId, DeliveryTimeoutS);
        Ensure(attrs.Keys.Order(StringComparer.Ordinal).SequenceEqual(DeadLetterKeys), $"the denied target's dead-letter attributes were {Sorted(attrs.Keys)}");
        Ensure(attrs["ERROR_CODE"] == "NO_PERMISSIONS", $"an SNS target with no resource policy dead-lettered as '{attrs["ERROR_CODE"]}'");
        Ensure(Shaped(attrs["ERROR_MESSAGE"]) == AccessDenied, $"the denied target's ERROR_MESSAGE was '{Shaped(attrs["ERROR_MESSAGE"])}'");
        Ensure(attrs["RULE_ARN"] == ruleArn, "the denied target's dead-letter RULE_ARN does not name the rule");
        Ensure(attrs["TARGET_ARN"] == topicArn, "the denied target's dead-letter TARGET_ARN does not name the topic");
        Ensure(JsonNode.DeepEquals(body, healthy), "the dead-lettered body differs from the event delivered to the healthy sibling");

        // Repair: subscribe a queue to the topic and grant the withheld statement.
        var (subUrl, subArn) = await Queue(ctx, sqs, "sub");
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = subUrl,
            Attributes = new()
            {
                ["Policy"] = $$$$"""{"Version": "2012-10-17", "Statement": [{"Effect": "Allow", "Principal": {"Service": "sns.amazonaws.com"}, "Action": "sqs:SendMessage", "Resource": "{{{{subArn}}}}", "Condition": {"ArnEquals": {"aws:SourceArn": "{{{{topicArn}}}}"}}}]}""",
            },
        });
        var subId = (await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "sqs",
            Endpoint = subArn,
            ReturnSubscriptionArn = true,
        })).SubscriptionArn;
        ctx.OnCleanup(() => sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subId }));
        await sns.SetTopicAttributesAsync(new SetTopicAttributesRequest
        {
            TopicArn = topicArn,
            AttributeName = "Policy",
            AttributeValue = $$$$"""{"Version": "2012-10-17", "Statement": [{"Effect": "Allow", "Principal": {"Service": "events.amazonaws.com"}, "Action": "sns:Publish", "Resource": "{{{{topicArn}}}}", "Condition": {"ArnEquals": {"aws:SourceArn": "{{{{ruleArn}}}}"}}}]}""",
        });

        var marker2 = FidelityContext.UniqueName("m2-");
        var published = await ResendUntilNotified(events, sqs, bus, source, marker2, subUrl, timeoutS: 150.0);
        Ensure(published is not null, "after the policy was repaired no notification reached the subscriber");
        var env = JsonNode.Parse((string)published["Message"]!);
        Ensure((string?)env?["source"] == source, $"the repaired delivery's envelope had source '{env?["source"]}'");
    }

    private static string Shaped(string? message)
    {
        message = RequestIdPattern().Replace(message ?? "", "Request ID: <id>;");
        message = UserPattern().Replace(message, "User: <id> is not authorized");
        message = ResourcePattern().Replace(message, "on resource: <id> because");
        return message;
    }

    [GeneratedRegex("Request ID: [^;]+;")]
    private static partial Regex RequestIdPattern();

    [GeneratedRegex("User: [^ ]+ is not authorized")]
    private static partial Regex UserPattern();

    [GeneratedRegex("on resource: [^ ]+ because")]
    private static partial Regex ResourcePattern();

    private static async Task<(string Url, string Arn)> Queue(FidelityContext ctx, AmazonSQSClient sqs, string label)
    {
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName($"sim2real-evb-snsdlq-{label}-"),
            Tags = new() { ["sim2real"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes["QueueArn"];
        return (url, arn);
    }

    private static Task SetSqsPolicy(AmazonSQSClient sqs, string url, string arn, string ruleArn, string effect) =>
        sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = url,
            Attributes = new()
            {
                ["Policy"] = $$$$"""{"Version": "2012-10-17", "Statement": [{"Effect": "{{{{effect}}}}", "Principal": {"Service": "events.amazonaws.com"}, "Action": "sqs:SendMessage", "Resource": "{{{{arn}}}}", "Condition": {"ArnEquals": {"aws:SourceArn": "{{{{ruleArn}}}}"}}}]}""",
            },
        });

    private static async Task<string> Put(AmazonEventBridgeClient events, string bus, string source, string marker)
    {
        var resp = await events.PutEventsAsync(new PutEventsRequest
        {
            Entries =
            [
                new PutEventsRequestEntry
                {
                    EventBusName = bus,
                    Source = source,
                    DetailType = "sim2real-snsdlq",
                    Detail = $$"""{"marker": "{{marker}}"}""",
                },
            ],
        });
        Ensure(resp.FailedEntryCount == 0, $"PutEvents rejected the entry: {string.Join(", ", (resp.Entries ?? []).Select(e => $"{{'ErrorCode': '{e.ErrorCode}'}}"))}");
        return (resp.Entries ?? throw new KeyNotFoundException("'Entries'"))[0].EventId;
    }

    private static async Task<List<(JsonNode? Body, Dictionary<string, string?> Attrs)>> ReceiveSqs(AmazonSQSClient sqs, string url)
    {
        var output = new List<(JsonNode?, Dictionary<string, string?>)>();
        var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = url,
            MaxNumberOfMessages = 10,
            WaitTimeSeconds = 5,
            MessageAttributeNames = ["All"],
        });
        foreach (var m in resp.Messages ?? [])
        {
            await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle });
            var body = JsonNode.Parse(m.Body);
            var attrs = (m.MessageAttributes ?? []).ToDictionary(kv => kv.Key, kv => (string?)kv.Value.StringValue, StringComparer.Ordinal);
            output.Add((body, attrs));
        }

        return output;
    }

    private static async Task<(JsonNode? Body, Dictionary<string, string?> Attrs)> AwaitSqs(
        AmazonSQSClient sqs, string url, Func<JsonNode?, Dictionary<string, string?>, bool> match, double timeoutS)
    {
        var started = FidelityTime.StartNew();
        while (started.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            foreach (var (body, attrs) in await ReceiveSqs(sqs, url))
            {
                if (match(body, attrs))
                {
                    return (body, attrs);
                }
            }
        }

        throw Fail($"no matching message reached the queue within {timeoutS:0.0}s");
    }

    /// <summary>Send events until a notification reaches <c>sub_url</c> (SNS policy propagation).</summary>
    private static async Task<JsonNode?> ResendUntilNotified(
        AmazonEventBridgeClient events, AmazonSQSClient sqs, string bus, string source, string marker, string subUrl, double timeoutS)
    {
        var started = FidelityTime.StartNew();
        var n = 0;
        while (started.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            n++;
            await Put(events, bus, source, $"{marker}-{n}");
            var msgs = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = subUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 })).Messages ?? [];
            foreach (var m in msgs)
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = subUrl, ReceiptHandle = m.ReceiptHandle });
                var note = JsonNode.Parse(m.Body);
                if ((string?)note?["Type"] == "Notification")
                {
                    return note;
                }
            }
        }

        return null;
    }

    private static string Sorted(IEnumerable<string> items) => "[" + string.Join(", ", items.Order(StringComparer.Ordinal).Select(i => $"'{i}'")) + "]";
}

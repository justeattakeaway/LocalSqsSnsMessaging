// Translated from behavioral/sns/check_clearing_a_filter_policy_with_an_empty_value_restores_unfiltered_delivery.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using SnsMessageAttributeValue = Amazon.SimpleNotificationService.Model.MessageAttributeValue;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.clearing-a-filter-policy-with-an-empty-value-restores-unfiltered-delivery</summary>
[Category("AwsEvaluationSuite")]
public sealed class ClearingAFilterPolicyWithAnEmptyValueRestoresUnfilteredDelivery
{
    private const string Scenario = "sns-filter-policy-clear";

    /// <summary>
    /// An empty FilterPolicy removes the attribute and the filter in effect: a blue message withheld
    /// under {"color": ["red"]} is delivered once the policy is cleared, as is one with no attributes.
    /// An unfiltered twin subscription bounds each wait.
    /// </summary>
    [Test]
    [DisplayName("sns.clearing-a-filter-policy-with-an-empty-value-restores-unfiltered-delivery")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topic = await CreateTopic(ctx);
        var (filteredUrl, filteredArn) = await CreateQueueForTopics(ctx, [topic]);
        var (twinUrl, twinArn) = await CreateQueueForTopics(ctx, [topic]);
        var sub = await SubscribeAndConfirm(ctx, topic, filteredArn,
            new() { ["FilterPolicy"] = new JsonObject { ["color"] = new JsonArray("red") }.ToJsonString() });
        await SubscribeAndConfirm(ctx, topic, twinArn);
        var runId = FidelityContext.UniqueName("sim2real-behav-sns-fpclear-");

        async Task Publish(string body, string? color)
        {
            var attrs = color is not null
                ? new Dictionary<string, SnsMessageAttributeValue> { ["color"] = new() { DataType = "String", StringValue = color } }
                : [];
            await sns.PublishAsync(new PublishRequest { TopicArn = topic, Message = body, MessageAttributes = attrs });
        }

        string blue = $"{runId}-blue-filtered", red = $"{runId}-red";
        await Publish(blue, "blue");
        await Publish(red, "red");
        await DrainUntil(ctx, twinUrl, [blue, red]);
        var seen = await DrainUntil(ctx, filteredUrl, [red]);
        Ensure(!seen.ContainsKey(blue), "the blue message was delivered through the red-only filter");

        await sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
        {
            SubscriptionArn = sub,
            AttributeName = "FilterPolicy",
            AttributeValue = "",
        });
        var attrs = (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = sub })).Attributes ?? [];
        Ensure(!attrs.ContainsKey("FilterPolicy"),
            $"an empty FilterPolicy must remove the attribute, it reads '{attrs.GetValueOrDefault("FilterPolicy")}'");

        string? seenBlue = null;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var body = $"{runId}-blue-after-clear-{attempt}";
            await Publish(body, "blue");
            await DrainUntil(ctx, twinUrl, [body]);
            var got = await ReceiveFor(ctx, filteredUrl, 10.0);
            if (got.Keys.Any(k => k.StartsWith($"{runId}-blue-after-clear-", StringComparison.Ordinal)))
            {
                seenBlue = body;
                break;
            }
        }

        Ensure(seenBlue is not null, "after clearing the FilterPolicy, 8 blue publishes were all still withheld");

        var bare = $"{runId}-no-attributes";
        await Publish(bare, null);
        await DrainUntil(ctx, filteredUrl, [bare]);
    }

    /// <summary>Receive (and delete) everything that arrives within windowS; key -> message.</summary>
    private static async Task<Dictionary<string, Message>> ReceiveFor(FidelityContext ctx, string url, double windowS)
    {
        var sqs = ctx.Sqs;
        var seen = new Dictionary<string, Message>();
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(windowS))
        {
            foreach (var msg in (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 })).Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle });
                seen[Key(msg)] = msg;
            }
        }

        return seen;
    }

    private static List<Amazon.SimpleNotificationService.Model.Tag> TagsKv() =>
        [new() { Key = "sim2real", Value = Scenario }, new() { Key = "sim2real:managed", Value = "true" }];

    /// <summary>CreateTopic (tagged + prefix-named) and register DeleteTopic immediately.</summary>
    private static async Task<string> CreateTopic(FidelityContext ctx, string prefix = "sim2real-behav-sns-t-")
    {
        var sns = ctx.Sns;
        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest { Name = FidelityContext.UniqueName(prefix), Tags = TagsKv() })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>Create a real SQS queue whose policy lets exactly these topics send to it.</summary>
    private static async Task<(string Url, string Arn)> CreateQueueForTopics(FidelityContext ctx, List<string> topicArns)
    {
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-sns-q-"),
            Tags = new() { ["sim2real"] = Scenario, ["sim2real:managed"] = "true" },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];
        var policy = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Effect"] = "Allow",
                ["Principal"] = new JsonObject { ["Service"] = "sns.amazonaws.com" },
                ["Action"] = "sqs:SendMessage",
                ["Resource"] = arn,
                ["Condition"] = new JsonObject
                {
                    ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = new JsonArray(topicArns.Select(t => (JsonNode)JsonValue.Create(t)).ToArray()) },
                },
            }),
        };
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = url, Attributes = new() { ["Policy"] = policy.ToJsonString() } });
        return (url, arn);
    }

    /// <summary>Subscribe an SQS queue, register Unsubscribe, poll until PendingConfirmation is false.</summary>
    private static async Task<string> SubscribeAndConfirm(FidelityContext ctx, string topicArn, string queueArn,
        Dictionary<string, string>? attributes = null, double timeoutS = 60.0)
    {
        var sns = ctx.Sns;
        var request = new SubscribeRequest { TopicArn = topicArn, Protocol = "sqs", Endpoint = queueArn, ReturnSubscriptionArn = true };
        if (attributes is { Count: > 0 })
        {
            request.Attributes = attributes;
        }

        var subArn = (await sns.SubscribeAsync(request)).SubscriptionArn;
        ctx.OnCleanup(() => UnsubscribeIfPresent(sns, subArn));
        var deadline = FidelityTime.StartNew();
        string? pending = null;
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            pending = (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn }))
                .Attributes?.GetValueOrDefault("PendingConfirmation");
            if (pending == "false")
            {
                return subArn;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1.0));
        }

        throw Fail($"the sqs subscription was still PendingConfirmation='{pending}' after {timeoutS}s");
    }

    /// <summary>Unsubscribe; a subscription already removed with its topic is the tolerated outcome.</summary>
    private static async Task UnsubscribeIfPresent(AmazonSimpleNotificationServiceClient sns, string subArn)
    {
        var error = await FidelityContext.CaptureError(() => sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subArn }));
        if (error is not null && error.Code != "NotFound")
        {
            throw error.Exception;
        }
    }

    /// <summary>The published text of a delivered SQS message: the envelope's Message, else the raw body.</summary>
    private static string Key(Message msg)
    {
        var body = msg.Body ?? "";
        JsonNode? env;
        try
        {
            env = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return body;
        }

        if (env is JsonObject obj && obj["Type"] is JsonValue type && type.TryGetValue<string>(out var t) && t == "Notification")
        {
            return (string?)obj["Message"] ?? "";
        }

        return body;
    }

    private static string Sorted(IEnumerable<string> values) =>
        "[" + string.Join(", ", values.Order(StringComparer.Ordinal).Select(v => $"'{v}'")) + "]";

    /// <summary>
    /// Receive (and delete) until every key in wanted has been seen; return key -> message
    /// for EVERY message seen on the way, so a caller can assert what else arrived.
    /// </summary>
    private static async Task<Dictionary<string, Message>> DrainUntil(FidelityContext ctx, string url, HashSet<string> wanted, double timeoutS = 90.0)
    {
        var sqs = ctx.Sqs;
        var seen = new Dictionary<string, Message>();
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS) && !wanted.IsSubsetOf(seen.Keys))
        {
            foreach (var msg in (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 5,
                MessageAttributeNames = ["All"],
                MessageSystemAttributeNames = ["All"],
            })).Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle });
                seen[Key(msg)] = msg;
            }
        }

        var missing = wanted.Except(seen.Keys).ToList();
        Ensure(missing.Count == 0, $"within {timeoutS}s the queue never received {Sorted(missing)}; it received {Sorted(seen.Keys)}");
        return seen;
    }
}

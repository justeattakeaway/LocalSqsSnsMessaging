// Translated from behavioral/sns/check_subscribe_with_an_unknown_attribute_rejected_and_creates_nothing.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.subscribe-with-an-unknown-attribute-rejected-and-creates-nothing</summary>
[Category("AwsEvaluationSuite")]
public sealed class SubscribeWithAnUnknownAttributeRejectedAndCreatesNothing
{
    private const string Scenario = "sns-subscribe-unknown-attribute";

    /// <summary>
    /// Subscribe with a misspelt RawMessageDelivery beside a valid FilterPolicy is refused with
    /// InvalidParameter and creates no subscription; spelt correctly it creates one subscription
    /// holding both attributes that delivers raw, filtered messages.
    /// </summary>
    [Test]
    [DisplayName("sns.subscribe-with-an-unknown-attribute-rejected-and-creates-nothing")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topic = await CreateTopic(ctx);
        var (url, queueArn) = await CreateQueueForTopics(ctx, [topic]);
        const string policy = """{"tier": ["gold"]}""";

        var (code, message) = await Error(() => sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topic,
            Protocol = "sqs",
            Endpoint = queueArn,
            ReturnSubscriptionArn = true,
            Attributes = new() { ["FilterPolicy"] = policy, ["RawMessgeDelivery"] = "true" },
        }));
        Ensure((code, message) == ("InvalidParameter", "Invalid parameter: Attributes Reason: Unknown attribute RawMessgeDelivery"),
            $"Subscribe with an unknown attribute name: got '{code}' '{message}'");

        var runId = FidelityContext.UniqueName("sim2real-behav-sns-subattr-");
        var orphan = $"{runId}-after-refusal";
        await sns.PublishAsync(new PublishRequest
        {
            TopicArn = topic,
            Message = orphan,
            MessageAttributes = new() { ["tier"] = new() { DataType = "String", StringValue = "gold" } },
        });

        var subs = await ListSubscriptions(sns, topic);
        Ensure(subs.Count == 0, $"the refused Subscribe must leave the topic with no subscription, it lists {subs.Count}");

        var sub = await SubscribeAndConfirm(ctx, topic, queueArn, new() { ["FilterPolicy"] = policy, ["RawMessageDelivery"] = "true" });
        var attrs = (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = sub })).Attributes ?? [];
        Ensure(JsonNode.DeepEquals(JsonNode.Parse(attrs.GetValueOrDefault("FilterPolicy", "null")), JsonNode.Parse("""{"tier": ["gold"]}"""))
                && attrs.GetValueOrDefault("RawMessageDelivery") == "true",
            "the corrected Subscribe must store both attributes, got FilterPolicy=" +
            $"'{attrs.GetValueOrDefault("FilterPolicy")}' RawMessageDelivery='{attrs.GetValueOrDefault("RawMessageDelivery")}'");
        var listed = (await ListSubscriptions(sns, topic)).Select(s => s.SubscriptionArn).ToList();
        Ensure(listed.SequenceEqual([sub]), $"exactly the corrected subscription must be listed, got {listed.Count}");

        var filtered = $"{runId}-filtered-out";
        var delivered = $"{runId}-delivered";
        await sns.PublishAsync(new PublishRequest
        {
            TopicArn = topic,
            Message = filtered,
            MessageAttributes = new() { ["tier"] = new() { DataType = "String", StringValue = "silver" } },
        });
        await sns.PublishAsync(new PublishRequest
        {
            TopicArn = topic,
            Message = delivered,
            MessageAttributes = new() { ["tier"] = new() { DataType = "String", StringValue = "gold" } },
        });
        var seen = await DrainUntil(ctx, url, [delivered]);
        var deliveredBody = seen[delivered].Body;
        Ensure(deliveredBody == delivered,
            $"the corrected subscription must deliver the bare body, got '{(deliveredBody.Length <= 120 ? deliveredBody : deliveredBody[..120])}'");
        Ensure(!seen.ContainsKey(orphan), "a publish made while no subscription existed was delivered");
        Ensure(!seen.ContainsKey(filtered), "the FilterPolicy of the corrected subscription did not apply");
    }

    private static async Task<List<Subscription>> ListSubscriptions(AmazonSimpleNotificationServiceClient sns, string topic)
    {
        var subs = new List<Subscription>();
        await foreach (var page in sns.Paginators.ListSubscriptionsByTopic(new ListSubscriptionsByTopicRequest { TopicArn = topic }).Responses)
        {
            subs.AddRange(page.Subscriptions ?? []);
        }

        return subs;
    }

    /// <summary>The (Code, Message) of the ClientError fn raises. An unexpected success raises too.</summary>
    private static async Task<(string? Code, string Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code, error.Message ?? "");
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
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes["QueueArn"];
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
                    ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = new JsonArray(topicArns.Select(t => (JsonNode)JsonValue.Create(t)!).ToArray()) },
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
            return obj["Message"] is JsonValue m && m.TryGetValue<string>(out var message) ? message : "";
        }

        return body;
    }

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
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 5,
                MessageAttributeNames = ["All"],
                MessageSystemAttributeNames = ["All"],
            });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle });
                seen[Key(msg)] = msg;
            }
        }

        var missing = wanted.Except(seen.Keys).Order(StringComparer.Ordinal).ToList();
        Ensure(missing.Count == 0,
            $"within {timeoutS}s the queue never received [{string.Join(", ", missing.Select(m => $"'{m}'"))}]; " +
            $"it received [{string.Join(", ", seen.Keys.Order(StringComparer.Ordinal).Select(k => $"'{k}'"))}]");
        return seen;
    }
}

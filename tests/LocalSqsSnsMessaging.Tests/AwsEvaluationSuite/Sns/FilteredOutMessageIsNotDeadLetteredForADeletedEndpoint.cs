// Translated from behavioral/sns/check_filtered_out_message_is_not_dead_lettered_for_a_deleted_endpoint.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using SnsMessageAttributeValue = Amazon.SimpleNotificationService.Model.MessageAttributeValue;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.filtered-out-message-is-not-dead-lettered-for-a-deleted-endpoint</summary>
[Category("AwsEvaluationSuite")]
public sealed class FilteredOutMessageIsNotDeadLetteredForADeletedEndpoint
{
    private const string Scenario = "sns-filtered-out-message-is-not-dead-lettere";
    private const string NoSuchQueue = "AWS.SimpleQueueService.NonExistentQueue";

    /// <summary>
    /// For a subscription whose queue was deleted, only messages its FilterPolicy admits are
    /// dead-lettered: the matching publish lands in the DLQ and the non-matching one never does,
    /// while an unfiltered subscription broken the same way dead-letters both.
    /// </summary>
    [Test]
    [DisplayName("sns.filtered-out-message-is-not-dead-lettered-for-a-deleted-endpoint")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var sqs = ctx.Sqs;
        var topicArn = await CreateTopic(ctx);
        var (dlqUrl, dlqArn) = await CreateQueueForTopic(ctx, topicArn);
        var (filtUrl, filtArn) = await CreateQueueForTopic(ctx, topicArn);
        var (allUrl, allArn) = await CreateQueueForTopic(ctx, topicArn);
        var filtSub = await SubscribeAndConfirm(ctx, topicArn, filtArn,
            attributes: new() { ["FilterPolicy"] = new JsonObject { ["route"] = new JsonArray("keep") }.ToJsonString() });
        var allSub = await SubscribeAndConfirm(ctx, topicArn, allArn, attributes: new() { ["RawMessageDelivery"] = "true" });
        foreach (var sub in new[] { filtSub, allSub })
        {
            await SetRedrive(ctx, sub, dlqArn);
        }

        await sqs.DeleteQueueAsync(new DeleteQueueRequest { QueueUrl = filtUrl });
        await sqs.DeleteQueueAsync(new DeleteQueueRequest { QueueUrl = allUrl });

        static Dictionary<string, SnsMessageAttributeValue> Route(string value) =>
            new() { ["route"] = new SnsMessageAttributeValue { DataType = "String", StringValue = value } };

        var drops = new List<string>();
        Dictionary<string, Message> got = [];
        var satisfied = false;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var keep = FidelityContext.UniqueName("sim2real-behav-sns-dlqfilt-keep-");
            var drop = FidelityContext.UniqueName("sim2real-behav-sns-dlqfilt-drop-");
            drops.Add(drop);
            await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = keep, MessageAttributes = Route("keep") });
            await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = drop, MessageAttributes = Route("drop") });
            var wanted = new HashSet<string> { $"raw:{keep}", $"raw:{drop}", $"env:{keep}" };
            got = await CollectKeyed(ctx, dlqUrl, DlqKey, wanted, windowS: 30.0);
            var filteredCopies = drops.Where(d => got.ContainsKey($"env:{d}")).ToList();
            Ensure(filteredCopies.Count == 0,
                "the filtered subscription must not dead-letter a message its FilterPolicy rejects; " +
                $"{filteredCopies.Count} rejected message(s) reached the DLQ in envelope form");
            if (wanted.IsSubsetOf(got.Keys))
            {
                satisfied = true;
                break;
            }
        }

        if (!satisfied)
        {
            throw Fail(
                "the DLQ never held both raw copies plus the filtered subscription's match across 6 " +
                $"attempts; last attempt's keys {PyList(got.Keys.Select(k => k[..Math.Min(60, k.Length)]).Order(StringComparer.Ordinal))}");
        }

        var late = (await Drain(ctx, dlqUrl, windowS: 20.0))
            .Where(b => drops.Any(d => b.Contains(d, StringComparison.Ordinal) && b != d)).ToList();
        Ensure(late.Count == 0,
            "a message the FilterPolicy rejected was dead-lettered by the filtered subscription " +
            $"within a 20s window after its unfiltered twin had already arrived: {PyList(late)}");
    }

    private static string PyList(IEnumerable<string> values) => $"[{string.Join(", ", values.Select(v => $"'{v}'"))}]";

    private static List<Amazon.SimpleNotificationService.Model.Tag> TagsKv() =>
        [new() { Key = "sim2real", Value = Scenario }, new() { Key = "sim2real:managed", Value = "true" }];

    /// <summary>CreateTopic (tagged + prefix-named) and register DeleteTopic immediately.</summary>
    private static async Task<string> CreateTopic(FidelityContext ctx, bool fifo = false)
    {
        var name = FidelityContext.UniqueName("sim2real-behav-sns-t-");
        var attrs = new Dictionary<string, string>();
        if (fifo)
        {
            name += ".fifo";
            attrs["FifoTopic"] = "true";
        }

        var arn = (await ctx.Sns.CreateTopicAsync(new CreateTopicRequest { Name = name, Attributes = attrs, Tags = TagsKv() })).TopicArn;
        ctx.OnCleanup(() => ctx.Sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>Create a real SQS queue whose policy lets exactly this topic send to it.</summary>
    private static async Task<(string Url, string Arn)> CreateQueueForTopic(FidelityContext ctx, string topicArn, bool fifo = false)
    {
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-q-");
        var attrs = new Dictionary<string, string>();
        if (fifo)
        {
            name += ".fifo";
            attrs["FifoQueue"] = "true";
        }

        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = name,
            Attributes = attrs,
            Tags = new() { ["sim2real"] = Scenario, ["sim2real:managed"] = "true" },
        })).QueueUrl;
        ctx.OnCleanup(() => DeleteQueueIfPresent(ctx, url));
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
                ["Condition"] = new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = topicArn } },
            }),
        };
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = url, Attributes = new() { ["Policy"] = policy.ToJsonString() } });
        return (url, arn);
    }

    /// <summary>DeleteQueue; a queue the check itself already deleted is the one tolerated outcome.</summary>
    private static async Task DeleteQueueIfPresent(FidelityContext ctx, string url)
    {
        var error = await FidelityContext.CaptureError(() => ctx.Sqs.DeleteQueueAsync(new DeleteQueueRequest { QueueUrl = url }));
        if (error is not null && error.Code != NoSuchQueue)
        {
            throw error.Exception;
        }
    }

    /// <summary>Subscribe an SQS queue and poll GetSubscriptionAttributes until it is confirmed.</summary>
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
        ctx.OnCleanup(() => ctx.Sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subArn }));
        await PollUntil(
            async () => (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn }))
                .Attributes?.GetValueOrDefault("PendingConfirmation"),
            pending => pending == "false",
            timeoutS: timeoutS, intervalS: 1.0,
            description: "the sqs subscription leaves PendingConfirmation");
        return subArn;
    }

    /// <summary>Bounded retry; RAISES on the deadline so a timeout can never read as a pass.</summary>
    private static async Task<T> PollUntil<T>(Func<Task<T>> fn, Func<T, bool> pred, double timeoutS, double intervalS, string description)
    {
        var deadline = FidelityTime.StartNew();
        var value = await fn();
        while (!pred(value))
        {
            if (deadline.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw Fail($"{description}: not satisfied within {timeoutS}s, last value '{value}'");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
            value = await fn();
        }

        return value;
    }

    /// <summary>The Message field of an SNS notification envelope, or None for a non-envelope body.</summary>
    private static string? EnvelopeMessage(Message msg)
    {
        JsonNode? body;
        try
        {
            body = JsonNode.Parse(msg.Body);
        }
        catch (JsonException)
        {
            return null;
        }

        if (body is JsonObject obj && obj["Type"] is JsonValue type && type.GetValueKind() == JsonValueKind.String && type.GetValue<string>() == "Notification")
        {
            return obj["Message"] is JsonValue m && m.GetValueKind() == JsonValueKind.String ? m.GetValue<string>() : obj["Message"]?.ToJsonString();
        }

        return null;
    }

    /// <summary>
    /// Long-poll for the whole window and return EVERY body seen, so the caller can assert on
    /// leakage rather than discard it.
    /// </summary>
    private static async Task<List<string>> Drain(FidelityContext ctx, string queueUrl, double windowS)
    {
        var sqs = ctx.Sqs;
        var bodies = new List<string>();
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(windowS))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                bodies.Add(msg.Body[..Math.Min(300, msg.Body.Length)]);
            }
        }

        return bodies;
    }

    /// <summary>Point the subscription's RedrivePolicy at the DLQ and wait for the read to agree.</summary>
    private static async Task SetRedrive(FidelityContext ctx, string subArn, string dlqArn)
    {
        var sns = ctx.Sns;
        var want = new JsonObject { ["deadLetterTargetArn"] = dlqArn };
        await sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
        {
            SubscriptionArn = subArn, AttributeName = "RedrivePolicy", AttributeValue = $"{{\"deadLetterTargetArn\": \"{dlqArn}\"}}",
        });
        await PollUntil(
            async () => (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn }))
                .Attributes?.GetValueOrDefault("RedrivePolicy"),
            raw => !string.IsNullOrEmpty(raw) && JsonNode.DeepEquals(JsonNode.Parse(raw), want),
            timeoutS: 30.0, intervalS: 1.0,
            description: "GetSubscriptionAttributes reflects the RedrivePolicy");
    }

    /// <summary>
    /// Long-poll until every wanted key has arrived or the window ends; returns what arrived
    /// (possibly partial). The caller decides whether partial is a failure.
    /// </summary>
    private static async Task<Dictionary<string, Message>> CollectKeyed(FidelityContext ctx, string queueUrl, Func<Message, string> key,
        HashSet<string> wanted, double windowS)
    {
        var sqs = ctx.Sqs;
        var got = new Dictionary<string, Message>();
        var deadline = FidelityTime.StartNew();
        while (!wanted.IsSubsetOf(got.Keys) && deadline.Elapsed < TimeSpan.FromSeconds(windowS))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5,
                MessageAttributeNames = ["All"], MessageSystemAttributeNames = ["All"],
            });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                got.TryAdd(key(msg), msg);
            }
        }

        return got;
    }

    /// <summary>'env:&lt;Message&gt;' for an enveloped copy, 'raw:&lt;Body&gt;' for a raw one.</summary>
    private static string DlqKey(Message msg)
    {
        var inner = EnvelopeMessage(msg);
        return inner is not null ? $"env:{inner}" : $"raw:{msg.Body}";
    }
}

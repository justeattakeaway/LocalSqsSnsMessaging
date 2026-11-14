// Translated from behavioral/sns/check_recreated_queue_with_the_same_name_resumes_delivery_on_the_old_subscription.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.recreated-queue-with-the-same-name-resumes-delivery-on-the-old-subscription</summary>
[Category("AwsEvaluationSuite")]
public sealed class RecreatedQueueWithTheSameNameResumesDeliveryOnTheOldSubscription
{
    private const string Scenario = "sns-recreated-queue-with-the-same-name-resum";

    /// <summary>
    /// A subscription binds to its queue by ARN: while the queue is deleted a publish goes to the
    /// subscription's DLQ, and once a same-named queue is recreated the unchanged subscription delivers
    /// to it; the message published in the gap stays in the DLQ and is not replayed.
    /// </summary>
    // timing:real-time — retention: 60 s https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_DeleteQueue.html
    [Test]
    [DisplayName("sns.recreated-queue-with-the-same-name-resumes-delivery-on-the-old-subscription")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [Category(ApplyDefaultCategoryAttribute.TimeBasedCategoryName)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.RealTimeBudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var sqs = ctx.Sqs;
        var topicArn = await CreateTopic(ctx);
        var (dlqUrl, dlqArn) = await CreateQueueForTopic(ctx, topicArn);
        var (qUrl, qArn) = await CreateQueueForTopic(ctx, topicArn);
        var qName = qArn[(qArn.LastIndexOf(':') + 1)..];
        var sub = await SubscribeAndConfirm(ctx, topicArn, qArn);
        await SetRedrive(ctx, sub, dlqArn);

        await sqs.DeleteQueueAsync(qUrl);
        var gapMarkers = new List<string>();
        var reachedDlq = false;
        for (var i = 0; i < 6; i++)
        {
            var gap = FidelityContext.UniqueName("sim2real-behav-sns-recreate-gap-");
            gapMarkers.Add(gap);
            await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = gap });
            if ((await CollectKeyed(ctx, dlqUrl, EnvelopeMessage, [gap], windowS: 20.0)).ContainsKey(gap))
            {
                reachedDlq = true;
                break;
            }
        }

        if (!reachedDlq)
        {
            throw Fail("no publish made while the queue was deleted reached the DLQ");
        }

        async Task<string?> Recreate()
        {
            var (created, error) = await FidelityContext.Capture(() => sqs.CreateQueueAsync(new CreateQueueRequest
            {
                QueueName = qName, Tags = new() { ["sim2real"] = Scenario, ["sim2real:managed"] = "true" },
            }));
            if (error is not null)
            {
                if (error.Code != "AWS.SimpleQueueService.QueueDeletedRecently")
                {
                    throw error.Exception;
                }

                return null;
            }

            return created!.QueueUrl;
        }

        var newUrl = (await PollUntil(Recreate, u => u is not null, timeoutS: 120.0, intervalS: 10.0,
            description: "a queue with the deleted queue's name can be created again"))!;
        ctx.OnCleanup(() => DeleteQueueIfPresent(ctx, newUrl));
        var newArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = newUrl, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];
        Ensure(newArn == qArn, "a queue recreated under the same name must have the same ARN");
        var policy = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Effect"] = "Allow", ["Principal"] = new JsonObject { ["Service"] = "sns.amazonaws.com" },
                ["Action"] = "sqs:SendMessage", ["Resource"] = newArn,
                ["Condition"] = new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = topicArn } },
            }),
        };
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = newUrl, Attributes = new() { ["Policy"] = policy.ToJsonString() } });

        var attrs = (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = sub })).Attributes ?? [];
        Ensure((attrs["Endpoint"], attrs["PendingConfirmation"]) == (qArn, "false"),
            "the original subscription must still target the queue ARN and stay confirmed, got " +
            $"PendingConfirmation='{attrs.GetValueOrDefault("PendingConfirmation")}'");

        var resumed = false;
        for (var i = 0; i < 8; i++)
        {
            var back = FidelityContext.UniqueName("sim2real-behav-sns-recreate-back-");
            await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = back });
            var got = await CollectKeyed(ctx, newUrl, EnvelopeMessage, [back], windowS: 15.0);
            if (got.ContainsKey(back))
            {
                resumed = true;
                break;
            }

            var stray = got.Keys.Where(gapMarkers.Contains).ToList();
            Ensure(stray.Count == 0, $"a gap message was replayed into the recreated queue: {List(stray)}");
        }

        if (!resumed)
        {
            throw Fail("the original subscription never delivered to the queue recreated under its name");
        }

        var replayed = (await Drain(ctx, newUrl, windowS: 10.0)).Where(b => gapMarkers.Any(g => b.Contains(g, StringComparison.Ordinal))).ToList();
        Ensure(replayed.Count == 0,
            "messages published while the queue was deleted must not be replayed into the " +
            $"recreated queue; got {replayed.Count}");
    }

    private static string List(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(s => $"'{s}'")) + "]";

    private static string Head(string s, int n) => s.Length <= n ? s : s[..n];

    private static List<Amazon.SimpleNotificationService.Model.Tag> TagsKv() =>
        [new() { Key = "sim2real", Value = Scenario }, new() { Key = "sim2real:managed", Value = "true" }];

    /// <summary>CreateTopic (tagged + prefix-named) and register DeleteTopic immediately.</summary>
    private static async Task<string> CreateTopic(FidelityContext ctx, bool fifo = false)
    {
        var sns = ctx.Sns;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-t-");
        var attrs = new Dictionary<string, string>();
        if (fifo)
        {
            name += ".fifo";
            attrs["FifoTopic"] = "true";
        }

        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest { Name = name, Attributes = attrs, Tags = TagsKv() })).TopicArn;
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
            QueueName = name, Attributes = attrs,
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
        var error = await FidelityContext.CaptureError(() => ctx.Sqs.DeleteQueueAsync(url));
        if (error is not null && error.Code != "AWS.SimpleQueueService.NonExistentQueue")
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

        if (body is JsonObject obj && obj["Type"] is JsonValue type && type.TryGetValue<string>(out var t) && t == "Notification")
        {
            return (string?)obj["Message"];
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
                bodies.Add(Head(msg.Body, 300));
            }
        }

        return bodies;
    }

    /// <summary>Point the subscription's RedrivePolicy at the DLQ and wait for the read to agree.</summary>
    private static async Task SetRedrive(FidelityContext ctx, string subArn, string dlqArn)
    {
        var sns = ctx.Sns;
        var redrive = new JsonObject { ["deadLetterTargetArn"] = dlqArn };
        await sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
        {
            SubscriptionArn = subArn, AttributeName = "RedrivePolicy", AttributeValue = redrive.ToJsonString(),
        });
        await PollUntil(
            async () => (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn }))
                .Attributes?.GetValueOrDefault("RedrivePolicy"),
            raw => !string.IsNullOrEmpty(raw) && JsonNode.DeepEquals(JsonNode.Parse(raw), redrive),
            timeoutS: 30.0, intervalS: 1.0,
            description: "GetSubscriptionAttributes reflects the RedrivePolicy");
    }

    /// <summary>
    /// Long-poll until every wanted key has arrived or the window ends; returns what arrived
    /// (possibly partial). The caller decides whether partial is a failure.
    /// </summary>
    private static async Task<Dictionary<string, Message>> CollectKeyed(FidelityContext ctx, string queueUrl, Func<Message, string?> key,
        HashSet<string> wanted, double windowS)
    {
        var sqs = ctx.Sqs;
        // Python keys this dict by key(msg), which may be None; "" never collides with a marker.
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
                got.TryAdd(key(msg) ?? "", msg);
            }
        }

        return got;
    }
}

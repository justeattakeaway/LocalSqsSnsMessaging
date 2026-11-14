// Translated from behavioral/sns/check_fifo_subscription_redrive_requires_a_fifo_dlq_and_dead_letters_into_it.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.fifo-subscription-redrive-requires-a-fifo-dlq-and-dead-letters-into-it</summary>
[Category("AwsEvaluationSuite")]
public sealed class FifoSubscriptionRedriveRequiresAFifoDlqAndDeadLettersIntoIt
{
    private const string Scenario = "sns-fifo-subscription-redrive-requires-a-fif";

    /// <summary>
    /// A FIFO subscription refuses a standard-queue DLQ with InvalidParameter and stores no
    /// RedrivePolicy; a FIFO DLQ is accepted, and once the subscribed queue is deleted the
    /// notification is dead-lettered into it under its original MessageGroupId.
    /// </summary>
    [Test]
    [DisplayName("sns.fifo-subscription-redrive-requires-a-fifo-dlq-and-dead-letters-into-it")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var sqs = ctx.Sqs;
        var topicArn = await CreateTopic(ctx, fifo: true);
        var (stdUrl, stdArn) = await CreateQueueForTopic(ctx, topicArn);
        var (dlqUrl, dlqArn) = await CreateQueueForTopic(ctx, topicArn, fifo: true);
        var (endUrl, endArn) = await CreateQueueForTopic(ctx, topicArn, fifo: true);
        var sub = await SubscribeAndConfirm(ctx, topicArn, endArn);

        var (code, message) = await Error(() => sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
        {
            SubscriptionArn = sub,
            AttributeName = "RedrivePolicy",
            AttributeValue = new JsonObject { ["deadLetterTargetArn"] = stdArn }.ToJsonString(),
        }));
        Ensure(code == "InvalidParameter"
            && message == "Invalid parameter: RedrivePolicy: must use a FIFO queue as DLQ for a FIFO " +
                "Subscription to a FIFO Topic.",
            $"a standard DLQ on a FIFO subscription must be refused, got '{code}' '{message}'");
        var attrs = (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = sub })).Attributes ?? [];
        Ensure(!attrs.ContainsKey("RedrivePolicy"),
            $"the refused RedrivePolicy must not be stored, got '{attrs.GetValueOrDefault("RedrivePolicy")}'");

        await SetRedrive(ctx, sub, dlqArn);
        await sqs.DeleteQueueAsync(endUrl);
        var group = FidelityContext.UniqueName("sim2real-grp-");
        Message? msg = null;
        for (var i = 0; i < 6; i++)
        {
            var dead = FidelityContext.UniqueName("sim2real-behav-sns-dlqfifo-dead-");
            await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = dead, MessageGroupId = group, MessageDeduplicationId = dead });
            var got = await CollectKeyed(ctx, dlqUrl, EnvelopeMessage, [dead], windowS: 30.0);
            if (got.TryGetValue(dead, out var found))
            {
                msg = found;
                break;
            }
        }

        if (msg is null)
        {
            throw Fail("no publish to the deleted FIFO endpoint reached the FIFO DLQ across 6 attempts");
        }

        var gotGroup = msg.Attributes?.GetValueOrDefault("MessageGroupId");
        Ensure(gotGroup == group,
            $"the dead-lettered copy must keep its MessageGroupId, got " +
            $"{(!string.IsNullOrEmpty(gotGroup) ? "a different group" : gotGroup is null ? "None" : $"'{gotGroup}'")}");
        var stray = await Drain(ctx, stdUrl, windowS: 5.0);
        Ensure(stray.Count == 0, $"the refused standard DLQ must receive nothing, got {Show(stray)}");
    }

    private static string Show(IEnumerable<string> values) => "[" + string.Join(", ", values.Select(v => $"'{v}'")) + "]";

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
            async () => (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn })).Attributes?.GetValueOrDefault("PendingConfirmation"),
            pending => pending == "false",
            timeoutS: timeoutS, intervalS: 1.0,
            description: "the sqs subscription leaves PendingConfirmation");
        return subArn;
    }

    /// <summary>Bounded retry; RAISES on the deadline so a timeout can never read as a pass.</summary>
    private static async Task<string?> PollUntil(Func<Task<string?>> fn, Func<string?, bool> pred, double timeoutS, double intervalS, string description)
    {
        var deadline = FidelityTime.StartNew();
        var value = await fn();
        while (!pred(value))
        {
            if (deadline.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw Fail($"{description}: not satisfied within {timeoutS}s, last value {(value is null ? "None" : $"'{value}'")}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
            value = await fn();
        }

        return value;
    }

    /// <summary>(Error.Code, Error.Message) the call raises. An unexpected success RAISES.</summary>
    private static async Task<(string Code, string Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code ?? "", error.Message ?? "");
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
                bodies.Add(msg.Body.Length <= 300 ? msg.Body : msg.Body[..300]);
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
            SubscriptionArn = subArn,
            AttributeName = "RedrivePolicy",
            AttributeValue = want.ToJsonString(),
        });
        await PollUntil(
            async () => (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn })).Attributes?.GetValueOrDefault("RedrivePolicy"),
            raw => !string.IsNullOrEmpty(raw) && JsonNode.DeepEquals(JsonNode.Parse(raw), want),
            timeoutS: 30.0, intervalS: 1.0,
            description: "GetSubscriptionAttributes reflects the RedrivePolicy");
    }

    /// <summary>
    /// Long-poll until every wanted key has arrived or the window ends; returns what arrived
    /// (possibly partial). The caller decides whether partial is a failure.
    /// </summary>
    private static async Task<Dictionary<string, Message>> CollectKeyed(FidelityContext ctx, string queueUrl, Func<Message, string?> key, HashSet<string> wanted, double windowS)
    {
        var sqs = ctx.Sqs;
        var got = new Dictionary<string, Message>();
        var deadline = FidelityTime.StartNew();
        while (!wanted.IsSubsetOf(got.Keys) && deadline.Elapsed < TimeSpan.FromSeconds(windowS))
        {
#pragma warning disable CS0618 // The check deliberately uses the legacy AttributeNames parameter.
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 5,
                MessageAttributeNames = ["All"],
                AttributeNames = ["All"],
            });
#pragma warning restore CS0618
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                // got.setdefault(key(msg), msg). Python also files a non-envelope body under a None
                // key, which no wanted key can match, so it is simply dropped here.
                if (key(msg) is { } k)
                {
                    got.TryAdd(k, msg);
                }
            }
        }

        return got;
    }
}

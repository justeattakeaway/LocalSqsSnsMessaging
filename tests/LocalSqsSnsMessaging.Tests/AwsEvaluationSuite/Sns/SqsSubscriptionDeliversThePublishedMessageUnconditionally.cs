// Translated from behavioral/sns/check_sqs_subscription_delivers_the_published_message_unconditionally.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.sqs-subscription-delivers-the-published-message-unconditionally</summary>
[Category("AwsEvaluationSuite")]
public sealed class SqsSubscriptionDeliversThePublishedMessageUnconditionally
{
    private const string Scenario = "sns-envelope-delivery";

    /// <summary>
    /// A message published to a topic with a confirmed SQS subscription arrives in the queue in the
    /// SNS envelope: Type=Notification, verbatim Message, Publish's MessageId, topic ARN, signature
    /// fields.
    /// </summary>
    [Test]
    [DisplayName("sns.sqs-subscription-delivers-the-published-message-unconditionally")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = await CreateTopic(ctx);
        var (queueUrl, queueArn) = await CreateQueueForTopic(ctx, topicArn);
        var subArn = await SubscribeAndConfirm(ctx, topicArn, queueArn);

        var payload = FidelityContext.UniqueName("sim2real-behav-sns-payload-");
        var messageId = (await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = payload })).MessageId;

        var body = await AwaitBody(ctx, queueUrl, payload);
        var envelope = JsonNode.Parse(body)!.AsObject();

        Ensure((string?)envelope["Type"] == "Notification", $"wrong envelope type: {envelope.ToJsonString()}");
        Ensure((string?)envelope["Message"] == payload,
            $"the delivered body is not what was published: '{envelope["Message"]}' != '{payload}'");
        Ensure((string?)envelope["MessageId"] == messageId,
            "the delivered MessageId does not match Publish: " +
            $"'{envelope["MessageId"]}' != '{messageId}'");
        Ensure((string?)envelope["TopicArn"] == topicArn,
            $"the envelope names the wrong topic: '{envelope["TopicArn"]}' != '{topicArn}'");
        string[] required = ["Type", "MessageId", "TopicArn", "Message", "Timestamp", "SignatureVersion", "Signature", "SigningCertURL"];
        var present = envelope.Select(kv => kv.Key).ToHashSet();
        Ensure(present.IsSupersetOf(required),
            $"the envelope is missing required fields {List(required.Except(present).Order(StringComparer.Ordinal))}; " +
            $"it carried {List(present.Order(StringComparer.Ordinal))}");
        Ensure(subArn.StartsWith(topicArn + ":", StringComparison.Ordinal),
            $"the confirmed SubscriptionArn is not a child of the topic: '{subArn}'");
    }

    private static string List(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(s => $"'{s}'")) + "]";

    private static List<Amazon.SimpleNotificationService.Model.Tag> TagsKv() =>
        [new() { Key = "sim2real", Value = Scenario }, new() { Key = "sim2real:managed", Value = "true" }];

    /// <summary>CreateTopic (tagged + prefix-named) and register DeleteTopic immediately; returns the ARN.</summary>
    private static async Task<string> CreateTopic(FidelityContext ctx, bool fifo = false, Dictionary<string, string>? attributes = null)
    {
        var sns = ctx.Sns;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-t-");
        var attrs = new Dictionary<string, string>(attributes ?? []);
        if (fifo)
        {
            name += ".fifo";
            attrs["FifoTopic"] = "true";
        }

        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest { Name = name, Attributes = attrs, Tags = TagsKv() })).TopicArn;
        ctx.OnCleanup(() => ctx.Sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>Create a real SQS queue whose policy lets exactly this topic publish to it.</summary>
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
        ctx.OnCleanup(() => ctx.Sqs.DeleteQueueAsync(url));
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

        throw Fail($"subscription '{subArn}' was still PendingConfirmation='{pending}' after {timeoutS}s");
    }

    /// <summary>Long-poll the queue until a body containing needle arrives; returns that body.</summary>
    private static async Task<string> AwaitBody(FidelityContext ctx, string queueUrl, string needle, double timeoutS = 90.0)
    {
        var sqs = ctx.Sqs;
        var seen = new List<string>();
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                var body = msg.Body ?? "";
                seen.Add(body);
                if (body.Contains(needle, StringComparison.Ordinal))
                {
                    return body;
                }
            }
        }

        throw Fail($"no message containing '{needle}' was delivered within {timeoutS}s; " +
            $"bodies seen: {List(seen.Select(b => b.Length <= 120 ? b : b[..120]))}");
    }
}

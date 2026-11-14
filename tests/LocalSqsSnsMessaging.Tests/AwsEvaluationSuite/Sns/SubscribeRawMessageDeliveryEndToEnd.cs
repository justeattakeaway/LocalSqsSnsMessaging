// Translated from behavioral/sns/check_subscribe_raw_message_delivery_end_to_end.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.subscribe-raw-message-delivery-end-to-end</summary>
[Category("AwsEvaluationSuite")]
public sealed class SubscribeRawMessageDeliveryEndToEnd
{
    private const string Scenario = "sns-raw-delivery-e2e";

    /// <summary>
    /// A RawMessageDelivery=true SQS subscription receives the published message body verbatim,
    /// with no SNS JSON envelope, as read from the destination queue.
    /// </summary>
    [Test]
    [DisplayName("sns.subscribe-raw-message-delivery-end-to-end")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = await CreateTopic(sns, ctx, FidelityContext.UniqueName("sim2real-behav-sns-raw-"));
        var (queueUrl, queueArn) = await CreateSqsQueueForSns(ctx, topicArn);

        var subArn = (await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "sqs",
            Endpoint = queueArn,
            Attributes = new() { ["RawMessageDelivery"] = "true" },
        })).SubscriptionArn;
        if (!string.IsNullOrEmpty(subArn) && subArn != "pending confirmation")
        {
            ctx.OnCleanup(() => sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subArn }));
        }

        Ensure(subArn.StartsWith(topicArn + ":", StringComparison.Ordinal), $"expected an auto-confirmed SubscriptionArn, got '{subArn}'");

        var payload = $"payload-{Guid.NewGuid()}";
        var resp = await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = payload });
        Ensure(!string.IsNullOrEmpty(resp.MessageId), $"Publish must return a MessageId, got {resp}");

        var messages = await PollQueueForMessages(ctx, queueUrl, minCount: 1, timeoutS: 60, intervalS: 2);
        var bodies = messages.Select(m => m.Body).ToList();
        Ensure(bodies.Contains(payload),
            $"expected raw message body '{payload}' in delivered messages [{string.Join(", ", bodies.Select(b => $"'{b}'"))}]");
    }

    private static List<Amazon.SimpleNotificationService.Model.Tag> TagsKv() =>
        [new() { Key = "sim2real", Value = Scenario }, new() { Key = "sim2real:managed", Value = "true" }];

    /// <summary>CreateTopic + register best-effort DeleteTopic cleanup; returns the TopicArn.</summary>
    private static async Task<string> CreateTopic(AmazonSimpleNotificationServiceClient client, FidelityContext ctx, string name,
        Dictionary<string, string>? attributes = null, List<Amazon.SimpleNotificationService.Model.Tag>? tags = null)
    {
        var request = new CreateTopicRequest { Name = name, Tags = tags ?? TagsKv() };
        if (attributes is { Count: > 0 })
        {
            request.Attributes = attributes;
        }

        var arn = (await client.CreateTopicAsync(request)).TopicArn;
        ctx.OnCleanup(() => client.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>Create a REAL SQS queue whose policy lets the topic publish to it; returns (queue_url, queue_arn).</summary>
    private static async Task<(string Url, string Arn)> CreateSqsQueueForSns(FidelityContext ctx, string topicArn, bool fifo = false)
    {
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-q-");
        var attributes = new Dictionary<string, string>();
        if (fifo)
        {
            name += ".fifo";
            attributes["FifoQueue"] = "true";
        }

        var queueUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = name, Attributes = attributes,
            Tags = new() { ["sim2real"] = Scenario, ["sim2real:managed"] = "true" },
        })).QueueUrl;
        ctx.OnCleanup(() => ctx.Sqs.DeleteQueueAsync(queueUrl));
        var queueArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = queueUrl, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];
        var policy = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Effect"] = "Allow",
                ["Principal"] = new JsonObject { ["Service"] = "sns.amazonaws.com" },
                ["Action"] = "sqs:SendMessage",
                ["Resource"] = queueArn,
                ["Condition"] = new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = topicArn } },
            }),
        };
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = queueUrl, Attributes = new() { ["Policy"] = policy.ToJsonString() } });
        return (queueUrl, queueArn);
    }

    /// <summary>Long-poll an SQS queue until min_count messages are collected (or timeout); deletes as it drains.</summary>
    private static async Task<List<Message>> PollQueueForMessages(FidelityContext ctx, string queueUrl, int minCount = 1, double timeoutS = 60, double intervalS = 2)
    {
        var sqs = ctx.Sqs;
        var collected = new List<Message>();
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS) && collected.Count < minCount)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = (int)intervalS });
            var msgs = resp.Messages ?? [];
            foreach (var m in msgs)
            {
                collected.Add(m);
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = m.ReceiptHandle });
            }

            if (msgs.Count == 0)
            {
                await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
            }
        }

        return collected;
    }
}

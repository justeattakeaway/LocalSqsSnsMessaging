// Translated from behavioral/sns/check_fifo_topic_ordering_delivery.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.fifo-topic-ordering-delivery</summary>
[Category("AwsEvaluationSuite")]
public sealed class FifoTopicOrderingDelivery
{
    private const string Scenario = "sns-fifo-ordering";

    /// <summary>
    /// Messages published to a FIFO topic under one MessageGroupId each return a SequenceNumber
    /// and arrive at a subscribed FIFO SQS queue in publish order.
    /// </summary>
    [Test]
    [DisplayName("sns.fifo-topic-ordering-delivery")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-fifo-order-") + ".fifo";
        var topicArn = await CreateTopic(sns, ctx, name,
            attributes: new() { ["FifoTopic"] = "true", ["ContentBasedDeduplication"] = "true" });
        var (queueUrl, queueArn) = await CreateSqsQueueForSns(ctx, topicArn, fifo: true);

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

        var groupId = FidelityContext.UniqueName("group-");
        var sent = Enumerable.Range(0, 3).Select(i => $"msg-{i}-{Guid.NewGuid()}").ToList();
        foreach (var body in sent)
        {
            var resp = await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = body, MessageGroupId = groupId });
            Ensure(!string.IsNullOrEmpty(resp.SequenceNumber),
                $"a FIFO topic publish must return a SequenceNumber: {{'MessageId': '{resp.MessageId}', 'SequenceNumber': '{resp.SequenceNumber}'}}");
        }

        var messages = await PollQueueForMessages(ctx, queueUrl, minCount: sent.Count, timeoutS: 90, intervalS: 2);
        var received = messages.Take(sent.Count).Select(m => m.Body).ToList();
        Ensure(received.SequenceEqual(sent), $"expected FIFO delivery order {Repr(sent)}, got {Repr(received)}");
    }

    private static string Repr(IEnumerable<string?> items) => "[" + string.Join(", ", items.Select(s => $"'{s}'")) + "]";

    private static List<Amazon.SimpleNotificationService.Model.Tag> TagsKv() =>
        [new() { Key = "sim2real", Value = Scenario }, new() { Key = "sim2real:managed", Value = "true" }];

    /// <summary>CreateTopic (tagged) + register best-effort DeleteTopic cleanup; returns the TopicArn.</summary>
    private static async Task<string> CreateTopic(AmazonSimpleNotificationServiceClient client, FidelityContext ctx, string name,
        Dictionary<string, string>? attributes = null)
    {
        var request = new CreateTopicRequest { Name = name, Tags = TagsKv() };
        if (attributes is { Count: > 0 })
        {
            request.Attributes = attributes;
        }

        var arn = (await client.CreateTopicAsync(request)).TopicArn;
        ctx.OnCleanup(() => client.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>Create a REAL, tagged SQS queue whose policy lets the topic publish to it; returns (queue_url, queue_arn).</summary>
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
            QueueName = name,
            Attributes = attributes,
            Tags = new() { ["sim2real"] = Scenario, ["sim2real:managed"] = "true" },
        })).QueueUrl;
        ctx.OnCleanup(() => ctx.Sqs.DeleteQueueAsync(queueUrl));
        var queueArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = queueUrl, AttributeNames = ["QueueArn"] })).Attributes["QueueArn"];
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

    /// <summary>Long-poll an SQS queue until min_count messages are collected; deletes as it drains; raises on timeout.</summary>
    private static async Task<List<Message>> PollQueueForMessages(FidelityContext ctx, string queueUrl, int minCount = 1, double timeoutS = 90, double intervalS = 2)
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

        if (collected.Count < minCount)
        {
            throw Fail($"only {collected.Count}/{minCount} messages arrived within {timeoutS}s: " +
                $"{Repr(collected.Select(m => m.Body))}");
        }

        return collected;
    }
}

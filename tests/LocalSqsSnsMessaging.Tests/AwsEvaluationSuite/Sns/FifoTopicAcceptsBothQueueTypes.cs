// Translated from behavioral/sns/check_fifo_topic_accepts_both_queue_types.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.fifo-topic-accepts-both-queue-types</summary>
[Category("AwsEvaluationSuite")]
public sealed class FifoTopicAcceptsBothQueueTypes
{
    private const string Check = "fifo-topic-accepts-both-queue-types";

    /// <summary>
    /// A FIFO topic accepts both FIFO and standard SQS queue subscriptions, Publish returns a
    /// SequenceNumber, and the standard queue receives the FIFO-topic message.
    /// </summary>
    [Test]
    [DisplayName("sns.fifo-topic-accepts-both-queue-types")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsFifo, "Publish to a FIFO topic returns no SequenceNumber")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-fifo-mixed-") + ".fifo";
        var topicArn = await CreateTopic(
            sns, ctx, name,
            attributes: new() { ["FifoTopic"] = "true", ["ContentBasedDeduplication"] = "true" },
            tags: TagsKv());

        var (_, fifoQueueArn) = await CreateSqsQueueForSns(ctx, topicArn, fifo: true);
        var (stdQueueUrl, stdQueueArn) = await CreateSqsQueueForSns(ctx, topicArn, fifo: false);

        var fifoSub = (await sns.SubscribeAsync(new SubscribeRequest { TopicArn = topicArn, Protocol = "sqs", Endpoint = fifoQueueArn })).SubscriptionArn;
        if (!string.IsNullOrEmpty(fifoSub) && fifoSub != "pending confirmation")
        {
            ctx.OnCleanup(() => sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = fifoSub }));
        }

        Ensure(fifoSub.StartsWith(topicArn + ":", StringComparison.Ordinal), fifoSub);

        var stdSub = (await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "sqs",
            Endpoint = stdQueueArn,
            Attributes = new() { ["RawMessageDelivery"] = "true" },
        })).SubscriptionArn;
        if (!string.IsNullOrEmpty(stdSub) && stdSub != "pending confirmation")
        {
            ctx.OnCleanup(() => sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = stdSub }));
        }

        Ensure(stdSub.StartsWith(topicArn + ":", StringComparison.Ordinal), stdSub);

        var payload = $"mixed-{Guid.NewGuid()}";
        var resp = await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = payload, MessageGroupId = FidelityContext.UniqueName("group-") });
        Ensure(!string.IsNullOrEmpty(resp.SequenceNumber), $"a FIFO topic publish must return a SequenceNumber: {resp}");

        var messages = await PollQueueForMessages(ctx, stdQueueUrl, minCount: 1, timeoutS: 90, intervalS: 2);
        var bodies = messages.Select(m => m.Body).ToList();
        Ensure(bodies.Contains(payload), $"standard queue subscribed to a FIFO topic must receive: [{string.Join(", ", bodies.Select(b => $"'{b}'"))}]");
    }

    private static List<Amazon.SimpleNotificationService.Model.Tag> TagsKv() =>
        [new() { Key = "sim2real", Value = Check }, new() { Key = "sim2real:managed", Value = "true" }];

    /// <summary>CreateTopic + register best-effort DeleteTopic cleanup; returns the TopicArn.</summary>
    private static async Task<string> CreateTopic(AmazonSimpleNotificationServiceClient client, FidelityContext ctx, string name,
        Dictionary<string, string>? attributes = null, List<Amazon.SimpleNotificationService.Model.Tag>? tags = null)
    {
        var request = new CreateTopicRequest { Name = name };
        if (attributes is { Count: > 0 })
        {
            request.Attributes = attributes;
        }

        if (tags is { Count: > 0 })
        {
            request.Tags = tags;
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
            QueueName = name,
            Attributes = attributes,
            Tags = new() { ["sim2real"] = Check, ["sim2real:managed"] = "true" },
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

        return collected;
    }
}

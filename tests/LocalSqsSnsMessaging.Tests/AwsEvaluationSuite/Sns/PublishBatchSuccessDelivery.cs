// Translated from behavioral/sns/check_publish_batch_success_delivery.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.publish-batch-success-delivery</summary>
[Category("AwsEvaluationSuite")]
public sealed class PublishBatchSuccessDelivery
{
    /// <summary>PublishBatch reports every entry as Successful and each message is actually delivered.</summary>
    [Test]
    [DisplayName("sns.publish-batch-success-delivery")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var name = FidelityContext.UniqueName("sim2real-behav-topic-batch-");
        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = name,
            Tags =
            [
                new() { Key = "sim2real", Value = "publish-batch-success-delivery" },
                new() { Key = "sim2real:managed", Value = "true" },
            ],
        })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        var (queueUrl, queueArn) = await CreateSqsQueueForSns(ctx, "batch-q-");
        var subArn = (await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = arn,
            Protocol = "sqs",
            Endpoint = queueArn,
            Attributes = new() { ["RawMessageDelivery"] = "true" },
        })).SubscriptionArn;
        ctx.OnCleanup(() => sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subArn }));

        var entries = Enumerable.Range(0, 3)
            .Select(i => new PublishBatchRequestEntry { Id = i.ToString(CultureInfo.InvariantCulture), Message = $"batch-msg-{i}-{Guid.NewGuid()}" })
            .ToList();
        var resp = await sns.PublishBatchAsync(new PublishBatchRequest { TopicArn = arn, PublishBatchRequestEntries = entries });
        var successfulIds = (resp.Successful ?? []).Select(e => e.Id).ToHashSet();
        Ensure(successfulIds.SetEquals(entries.Select(e => e.Id)),
            $"expected every entry id in Successful, got [{string.Join(", ", (resp.Successful ?? []).Select(s => $"{{'Id': '{s.Id}', 'MessageId': '{s.MessageId}'}}"))}]");
        Ensure(resp.Failed is not { Count: > 0 },
            $"expected no Failed entries, got [{string.Join(", ", (resp.Failed ?? []).Select(f => $"{{'Id': '{f.Id}', 'Code': '{f.Code}', 'Message': '{f.Message}'}}"))}]");

        var messages = await PollQueueForMessages(ctx, queueUrl, minCount: 3, timeoutS: 30, intervalS: 2);
        var bodies = messages.Select(m => m.Body).ToHashSet();
        foreach (var e in entries)
        {
            Ensure(bodies.Contains(e.Message),
                $"missing batch message '{e.Message}' in delivered set {{{string.Join(", ", bodies.Select(b => $"'{b}'"))}}}");
        }
    }

    /// <summary>Poll probe() until predicate(result) is truthy; return the final result.</summary>
    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate,
        double timeoutS = 60, double intervalS = 1.0, string description = "condition")
    {
        var deadline = FidelityTime.StartNew();
        T? last = default;
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"{description} not met within {timeoutS}s. Last result: {last}");
    }

    /// <summary>
    /// Create an SQS queue with a policy allowing SNS to deliver to it.
    /// Returns (queue_url, queue_arn).
    /// </summary>
    private static async Task<(string Url, string Arn)> CreateSqsQueueForSns(FidelityContext ctx, string namePrefix = "sqs-target-", bool fifo = false)
    {
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-" + namePrefix);
        var attrs = new Dictionary<string, string>();
        if (fifo)
        {
            if (!name.EndsWith(".fifo", StringComparison.Ordinal))
            {
                name += ".fifo";
            }

            attrs["FifoQueue"] = "true";
        }

        var queueUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Attributes = attrs })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(queueUrl));
        await sqs.TagQueueAsync(new TagQueueRequest
        {
            QueueUrl = queueUrl,
            Tags = new() { ["sim2real"] = "publish-batch-success-delivery", ["sim2real:managed"] = "true" },
        });
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = queueUrl, AttributeNames = ["QueueArn"] })).Attributes["QueueArn"];
        var policy = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Sid"] = "AllowSNS",
                ["Effect"] = "Allow",
                ["Principal"] = new JsonObject { ["Service"] = "sns.amazonaws.com" },
                ["Action"] = "sqs:SendMessage",
                ["Resource"] = arn,
            }),
        };
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = queueUrl, Attributes = new() { ["Policy"] = policy.ToJsonString() } });
        return (queueUrl, arn);
    }

    /// <summary>
    /// Receive up to max_messages from a queue in a single poll call.
    /// Received messages are deleted by default. This matters for FIFO queues: a
    /// message group is locked while one of its messages is in flight, so an
    /// ordering test that does not delete would only ever see the first message.
    /// </summary>
    private static async Task<List<Message>> DrainQueue(FidelityContext ctx, string queueUrl, int maxMessages = 10, int waitTime = 2, bool delete = true)
    {
        var sqs = ctx.Sqs;
        var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = maxMessages,
            WaitTimeSeconds = waitTime,
            MessageAttributeNames = ["All"],
        });
        var messages = resp.Messages ?? [];
        if (delete && messages.Count > 0)
        {
            await sqs.DeleteMessageBatchAsync(new DeleteMessageBatchRequest
            {
                QueueUrl = queueUrl,
                Entries = messages.Select((m, i) => new DeleteMessageBatchRequestEntry { Id = i.ToString(CultureInfo.InvariantCulture), ReceiptHandle = m.ReceiptHandle }).ToList(),
            });
        }

        return messages;
    }

    /// <summary>Poll a queue, accumulating (and deleting) messages, until min_count arrive.</summary>
    private static async Task<List<Message>> PollQueueForMessages(FidelityContext ctx, string queueUrl, int minCount = 1, double timeoutS = 60, double intervalS = 2)
    {
        var collected = new List<Message>();

        async Task<List<Message>> Probe()
        {
            collected.AddRange(await DrainQueue(ctx, queueUrl));
            return collected;
        }

        await PollUntil(Probe, c => c.Count >= minCount, timeoutS: timeoutS, intervalS: intervalS);
        return collected;
    }
}

// Translated from behavioral/sns/check_fifo_sequence_numbers_monotonic_within_group.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.fifo-sequence-numbers-monotonic-within-group</summary>
[Category("AwsEvaluationSuite")]
public sealed class FifoSequenceNumbersMonotonicWithinGroup
{
    private const string Check = "fifo-sequence-numbers-monotonic-within-group";

    /// <summary>
    /// Publishes to one MessageGroupId on a FIFO topic get strictly increasing SequenceNumbers, and
    /// a real subscribed queue independently observes delivery preserve both that order and those
    /// SequenceNumbers.
    /// </summary>
    [Test]
    [DisplayName("sns.fifo-sequence-numbers-monotonic-within-group")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsFifo, "Publish to a FIFO topic returns no SequenceNumber")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicName = FidelityContext.UniqueName("sim2real-behav-sns-fifoord-") + ".fifo";
        var topicArn = await CreateTopic(sns, ctx, topicName, attributes: new() { ["FifoTopic"] = "true" },
            tags: [new() { Key = "sim2real-check", Value = Check }]);
        var (queueUrl, queueArn) = await CreateFifoQueueForSns(ctx, topicArn);
        await SubscribeQueue(sns, ctx, topicArn, queueArn);

        var seqs = new List<BigInteger>();
        var bodies = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            var body = $"seq-{i}-{Guid.NewGuid().ToString("N")[..8]}";
            var resp = await sns.PublishAsync(new PublishRequest
            {
                TopicArn = topicArn,
                Message = body,
                MessageGroupId = "group-a",
                MessageDeduplicationId = FidelityContext.UniqueName($"sim2real-behav-ord-{i}-"),
            });
            var seq = resp.SequenceNumber;
            Ensure(!string.IsNullOrEmpty(seq), $"expected SequenceNumber on FIFO publish #{i}, got {resp}");
            seqs.Add(BigInteger.Parse(seq, CultureInfo.InvariantCulture));
            bodies.Add(body);
        }

        Ensure(Enumerable.Range(0, seqs.Count - 1).All(i => seqs[i] < seqs[i + 1]),
            $"expected strictly increasing SequenceNumbers within a message group, got [{string.Join(", ", seqs)}]");

        var messages = await PollQueueForMessages(ctx, queueUrl, minCount: 5, timeoutS: 90, intervalS: 2);
        Ensure(messages.Count == 5, $"expected 5 messages delivered to the subscribed queue, got {messages.Count}");
        var envelopes = messages.Select(m => JsonNode.Parse(m.Body)!.AsObject()).ToList();
        var receivedBodies = envelopes.Select(e => (string?)e["Message"]).ToList();
        Ensure(receivedBodies.SequenceEqual(bodies),
            $"expected FIFO delivery to preserve publish order within the group, sent {List(bodies)}, got {List(receivedBodies)}");
        var receivedSeqs = envelopes.Select(e => BigInteger.Parse((string)e["SequenceNumber"]!, CultureInfo.InvariantCulture)).ToList();
        Ensure(receivedSeqs.SequenceEqual(seqs),
            "expected the delivered envelope's SequenceNumber to match the publish response in order, " +
            $"sent [{string.Join(", ", seqs)}], got [{string.Join(", ", receivedSeqs)}]");
    }

    private static string List(IEnumerable<string?> items) => "[" + string.Join(", ", items.Select(s => $"'{s}'")) + "]";

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

    /// <summary>Create a REAL FIFO SQS queue whose policy lets the topic publish to it; returns (queue_url, queue_arn).</summary>
    private static async Task<(string Url, string Arn)> CreateFifoQueueForSns(FidelityContext ctx, string topicArn)
    {
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-fifoord-q-") + ".fifo";
        var queueUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = name,
            Attributes = new() { ["FifoQueue"] = "true" },
            Tags = new() { ["sim2real-check"] = Check },
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

    /// <summary>
    /// Subscribe an sqs-protocol endpoint (non-raw, so the JSON envelope carries SequenceNumber);
    /// register unsubscribe cleanup.
    /// </summary>
    private static async Task<string> SubscribeQueue(AmazonSimpleNotificationServiceClient client, FidelityContext ctx, string topicArn, string queueArn)
    {
        var subArn = (await client.SubscribeAsync(new SubscribeRequest { TopicArn = topicArn, Protocol = "sqs", Endpoint = queueArn })).SubscriptionArn;
        if (!string.IsNullOrEmpty(subArn) && subArn != "pending confirmation")
        {
            ctx.OnCleanup(() => client.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subArn }));
        }

        return subArn;
    }

    /// <summary>Long-poll an SQS queue until min_count messages are collected (or timeout); deletes as it drains.</summary>
    private static async Task<List<Message>> PollQueueForMessages(FidelityContext ctx, string queueUrl, int minCount, double timeoutS = 90, double intervalS = 2)
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

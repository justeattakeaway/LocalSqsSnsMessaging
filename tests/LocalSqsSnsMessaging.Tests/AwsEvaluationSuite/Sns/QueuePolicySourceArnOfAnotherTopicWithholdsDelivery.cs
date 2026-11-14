// Translated from behavioral/sns/check_queue_policy_source_arn_of_another_topic_withholds_delivery.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.queue-policy-source-arn-of-another-topic-withholds-delivery</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueuePolicySourceArnOfAnotherTopicWithholdsDelivery
{
    private const string Check = "queue-policy-source-arn-of-another-topic-withholds-delivery";
    private const int Rounds = 3;

    /// <summary>
    /// SNS presents the publishing topic's ARN as `aws:SourceArn`: a queue whose policy admits only
    /// topic A receives A's messages and none from subscribed, confirmed topic B, while a control queue
    /// admitting B receives them; adding B's ARN to the condition makes the same queue receive B.
    /// </summary>
    [Test]
    [DisplayName("sns.queue-policy-source-arn-of-another-topic-withholds-delivery")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsDeliveryFailures, "SNS delivers to a queue whose policy admits only another topic as aws:SourceArn")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var sqs = ctx.Sqs;

        var topicA = await CreateTopic(ctx, "sim2real-behav-sns-srca-");
        var topicB = await CreateTopic(ctx, "sim2real-behav-sns-srcb-");
        var (sharedUrl, sharedArn) = await CreateQueue(ctx, "sim2real-behav-sns-shared-");
        var (controlUrl, controlArn) = await CreateQueue(ctx, "sim2real-behav-sns-ctl-");

        await SetQueuePolicy(sqs, sharedUrl, sharedArn, [topicA]);
        await SetQueuePolicy(sqs, controlUrl, controlArn, [topicB]);

        foreach (var (topic, queue) in new[] { (topicA, sharedArn), (topicB, sharedArn), (topicB, controlArn) })
        {
            var sub = (await sns.SubscribeAsync(new SubscribeRequest { TopicArn = topic, Protocol = "sqs", Endpoint = queue, ReturnSubscriptionArn = true })).SubscriptionArn;
            ctx.OnCleanup(() => sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = sub }));
            var pending = (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = sub })).Attributes["PendingConfirmation"];
            Ensure(pending == "false", $"an sqs subscription must be confirmed at once; PendingConfirmation='{pending}'");
        }

        var seenShared = new List<(string Topic, string Message)>();
        for (var i = 0; i < Rounds; i++)
        {
            var bodyB = FidelityContext.UniqueName($"sim2real-b{i}-");
            var bodyA = FidelityContext.UniqueName($"sim2real-a{i}-");
            var midB = (await sns.PublishAsync(new PublishRequest { TopicArn = topicB, Message = bodyB })).MessageId;
            Ensure(!string.IsNullOrEmpty(midB), "Publish to topic B must return a MessageId even though one subscriber will not get it");
            var got = await DrainUntil(sqs, controlUrl, [bodyB], 60);
            Ensure(got.SequenceEqual([(topicB, bodyB)]), $"control queue must receive B's round-{i} message, got {Repr(got)}");
            await sns.PublishAsync(new PublishRequest { TopicArn = topicA, Message = bodyA });
            seenShared.AddRange(await DrainUntil(sqs, sharedUrl, [bodyA], 60));
            Ensure(seenShared.Contains((topicA, bodyA)),
                $"the shared queue must receive topic A's round-{i} message; it received {Repr(seenShared)}");
        }

        var leaked = seenShared.Where(m => m.Topic == topicB).ToList();
        Ensure(leaked.Count == 0,
            "the shared queue's policy admits only topic A as aws:SourceArn, yet it received " +
            $"{leaked.Count} message(s) published to topic B");
        Ensure(seenShared.Select(m => m.Topic).Order(StringComparer.Ordinal).SequenceEqual(Enumerable.Repeat(topicA, Rounds)),
            $"the shared queue must hold exactly {Rounds} messages from topic A, got {seenShared.Count}");

        // Repair: admit B too. The same queue then receives B's next message.
        await SetQueuePolicy(sqs, sharedUrl, sharedArn, [topicA, topicB]);
        var deadline = FidelityTime.StartNew();
        var delivered = new List<(string Topic, string Message)>();
        var repaired = false;
        while (deadline.Elapsed < TimeSpan.FromSeconds(120))
        {
            var body = FidelityContext.UniqueName("sim2real-b-repair-");
            await sns.PublishAsync(new PublishRequest { TopicArn = topicB, Message = body });
            delivered = await DrainUntil(sqs, sharedUrl, [body], 20);
            if (delivered.Contains((topicB, body)))
            {
                repaired = true;
                break;
            }
        }

        if (!repaired)
        {
            throw Fail("after the queue policy admitted topic B, no message published to B reached the " +
                $"shared queue within the deadline; last drain {Repr(delivered)}");
        }
    }

    private static string Repr(IEnumerable<(string Topic, string Message)> items) =>
        "[" + string.Join(", ", items.Select(i => $"('{i.Topic}', '{i.Message}')")) + "]";

    private static async Task<string> CreateTopic(FidelityContext ctx, string prefix)
    {
        var sns = ctx.Sns;
        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = FidelityContext.UniqueName(prefix), Tags = [new() { Key = "sim2real", Value = Check }],
        })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    private static async Task<(string Url, string Arn)> CreateQueue(FidelityContext ctx, string prefix)
    {
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName(prefix), Tags = new() { ["sim2real"] = Check } })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];
        return (url, arn);
    }

    private static async Task SetQueuePolicy(AmazonSQSClient sqs, string url, string arn, List<string> topics)
    {
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
                    ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = new JsonArray(topics.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()) },
                },
            }),
        }.ToJsonString();
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = url, Attributes = new() { ["Policy"] = policy } });
        var deadline = FidelityTime.StartNew();
        while (true)
        {
            var stored = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["Policy"] })).Attributes?.GetValueOrDefault("Policy") ?? "{}";
            if (JsonNode.DeepEquals(Norm(JsonNode.Parse(stored)), Norm(JsonNode.Parse(policy))))
            {
                break;
            }

            Ensure(deadline.Elapsed < TimeSpan.FromSeconds(60), "the queue policy never read back as set");
            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>
    /// A policy document with every one-element list collapsed to its element and every list
    /// sorted, the form SQS stores; two documents are equal when their normal forms are.
    /// </summary>
    private static JsonNode? Norm(JsonNode? doc)
    {
        switch (doc)
        {
            case JsonObject obj:
                var normalized = new JsonObject();
                foreach (var (k, v) in obj)
                {
                    normalized[k] = Norm(v);
                }

                return normalized;
            case JsonArray arr:
                var items = arr.Select(Norm).ToList();
                return items.Count == 1
                    ? items[0]
                    : new JsonArray(items.OrderBy(i => i?.ToJsonString() ?? "null", StringComparer.Ordinal).ToArray());
            default:
                return doc?.DeepClone();
        }
    }

    /// <summary>
    /// Receive and delete until every body in <c>wanted</c> has arrived or the deadline passes;
    /// returns (TopicArn, Message) for every notification drained.
    /// </summary>
    private static async Task<List<(string Topic, string Message)>> DrainUntil(AmazonSQSClient sqs, string url, HashSet<string> wanted, double timeoutS)
    {
        var output = new List<(string Topic, string Message)>();
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS) && !wanted.IsSubsetOf(output.Select(m => m.Message)))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
            foreach (var msg in resp.Messages ?? [])
            {
                var env = JsonNode.Parse(msg.Body)!;
                output.Add(((string)env["TopicArn"]!, (string)env["Message"]!));
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle });
            }
        }

        return output;
    }
}

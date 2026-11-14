// Translated from behavioral/sns/check_fifo_archive_replay_redelivers_archived_messages_to_a_new_subscription.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.fifo-archive-replay-redelivers-archived-messages-to-a-new-subscription</summary>
[Category("AwsEvaluationSuite")]
public sealed class FifoArchiveReplayRedeliversArchivedMessagesToANewSubscription
{
    private const string Scenario = "sns-fifo-archive-replay";

    /// <summary>
    /// A FIFO topic with an ArchivePolicy replays pre-subscription messages, in publish order, to a
    /// new subscription whose ReplayPolicy starts at BeginningArchiveTime (ReplayStatus Completed);
    /// a twin subscription without a ReplayPolicy receives only later messages.
    /// </summary>
    [Test]
    [DisplayName("sns.fifo-archive-replay-redelivers-archived-messages-to-a-new-subscription")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var sqs = ctx.Sqs;
        var topicArn = await CreateTopic(
            ctx, fifo: true,
            attributes: new() { ["ArchivePolicy"] = new JsonObject { ["MessageRetentionPeriod"] = "1" }.ToJsonString() });
        // A topic with an ArchivePolicy cannot be deleted (InvalidState), so the policy is cleared
        // first; LIFO runs this before the DeleteTopic _create_topic registered.
        ctx.OnCleanup(() => sns.SetTopicAttributesAsync(new SetTopicAttributesRequest
        {
            TopicArn = topicArn, AttributeName = "ArchivePolicy", AttributeValue = "{}",
        }));
        var attrs = (await sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = topicArn })).Attributes ?? [];
        Ensure(JsonNode.DeepEquals(JsonNode.Parse(attrs.GetValueOrDefault("ArchivePolicy", "{}")), new JsonObject { ["MessageRetentionPeriod"] = "1" }),
            $"ArchivePolicy read back as {Repr(attrs.GetValueOrDefault("ArchivePolicy"))}");
        var beginning = attrs.GetValueOrDefault("BeginningArchiveTime");
        Ensure(!string.IsNullOrEmpty(beginning), $"an archiving topic must report BeginningArchiveTime, got {Show(attrs)}");

        const string group = "ledger";
        var archived = Enumerable.Range(1, 3).Select(i => FidelityContext.UniqueName($"sim2real-behav-replay-archived-{i}-")).ToList();
        foreach (var body in archived)
        {
            await sns.PublishAsync(new PublishRequest
            {
                TopicArn = topicArn, Message = body, MessageGroupId = group,
                MessageDeduplicationId = FidelityContext.UniqueName("dedup-"),
            });
        }

        var (replayUrl, replayArn) = await CreateQueueForTopic(ctx, topicArn, fifo: true);
        var (plainUrl, plainArn) = await CreateQueueForTopic(ctx, topicArn, fifo: true);
        var replayPolicy = new JsonObject { ["PointType"] = "Timestamp", ["StartingPoint"] = beginning };
        var replaySub = await SubscribeAndConfirm(
            ctx, topicArn, replayArn,
            attributes: new() { ["RawMessageDelivery"] = "true", ["ReplayPolicy"] = replayPolicy.ToJsonString() });
        await SubscribeAndConfirm(ctx, topicArn, plainArn,
            attributes: new() { ["RawMessageDelivery"] = "true" });

        // A new subscription takes a moment to join the delivery path, so the live leg republishes
        // a fresh live message (bounded) until the no-replay twin receives one. Every live message
        // is in the same group as the archived ones and published after them, so FIFO order makes
        // the first live arrival the proof that no archived message is still in flight for it.
        var lives = new List<string>();
        var plain = new List<string>();
        var deadline = FidelityTime.StartNew();
        while (!plain.Any(lives.Contains))
        {
            if (deadline.Elapsed >= TimeSpan.FromSeconds(120.0))
            {
                throw Fail($"the no-replay subscription received no live message within 120s across " +
                    $"{lives.Count} live publishes; it received {Show(plain)}");
            }

            var live = FidelityContext.UniqueName("sim2real-behav-replay-live-");
            await sns.PublishAsync(new PublishRequest
            {
                TopicArn = topicArn, Message = live, MessageGroupId = group,
                MessageDeduplicationId = FidelityContext.UniqueName("dedup-"),
            });
            lives.Add(live);
            var attemptEnd = FidelityTime.StartNew();
            while (attemptEnd.Elapsed < TimeSpan.FromSeconds(10.0) && !plain.Any(lives.Contains))
            {
                foreach (var msg in (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = plainUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 })).Messages ?? [])
                {
                    await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = plainUrl, ReceiptHandle = msg.ReceiptHandle });
                    plain.Add(msg.Body);
                }
            }
        }

        var backfilled = plain.Where(archived.Contains).ToList();
        Ensure(backfilled.Count == 0,
            $"a subscription without a ReplayPolicy must not be backfilled with messages " +
            $"published before it existed, but it received {Show(backfilled)}");
        Ensure(plain.All(lives.Contains), $"the no-replay subscription received unknown bodies {Show(plain)}");

        var replayed = new List<string>();
        deadline = FidelityTime.StartNew();
        while (!(archived.All(replayed.Contains) && replayed.Contains(lives[^1])))
        {
            if (deadline.Elapsed >= TimeSpan.FromSeconds(120.0))
            {
                throw Fail($"the replaying subscription did not receive the archived messages and the " +
                    $"last live one within 120s; received {Show(replayed)}");
            }

            foreach (var msg in (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = replayUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 })).Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = replayUrl, ReceiptHandle = msg.ReceiptHandle });
                replayed.Add(msg.Body);
            }
        }

        Ensure(replayed.Count == replayed.Distinct().Count() && replayed.All(b => archived.Contains(b) || lives.Contains(b)),
            $"the replaying subscription must receive each message at most once and only " +
            $"published ones, got {Show(replayed)}");
        Ensure(replayed.Where(archived.Contains).SequenceEqual(archived),
            $"archived messages must be replayed in publish order {Show(archived)}, got {Show(replayed)}");

        var status = await PollUntil(
            async () => (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = replaySub })).Attributes?.GetValueOrDefault("ReplayStatus"),
            v => v == "Completed", timeoutS: 60.0, intervalS: 2.0,
            description: "the replaying subscription reports ReplayStatus Completed");
        Ensure(status == "Completed", $"ReplayStatus is {Repr(status)}");
        var stored = (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = replaySub })).Attributes ?? [];
        Ensure(JsonNode.DeepEquals(JsonNode.Parse(stored.GetValueOrDefault("ReplayPolicy", "{}")), replayPolicy),
            $"ReplayPolicy read back as {Repr(stored.GetValueOrDefault("ReplayPolicy"))}, not {replayPolicy.ToJsonString()}");
    }

    private static string Repr(string? value) => value is null ? "None" : $"'{value}'";

    private static string Show(IEnumerable<string> values) => "[" + string.Join(", ", values.Select(v => $"'{v}'")) + "]";

    private static string Show(Dictionary<string, string> d) => "{" + string.Join(", ", d.Select(kv => $"'{kv.Key}': '{kv.Value}'")) + "}";

    private static List<Amazon.SimpleNotificationService.Model.Tag> TagsKv() =>
        [new() { Key = "sim2real", Value = Scenario }, new() { Key = "sim2real:managed", Value = "true" }];

    /// <summary>CreateTopic (tagged + prefix-named) and register DeleteTopic immediately.</summary>
    private static async Task<string> CreateTopic(FidelityContext ctx, bool fifo = false, Dictionary<string, string>? attributes = null)
    {
        var sns = ctx.Sns;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-t-") + (fifo ? ".fifo" : "");
        var attrs = new Dictionary<string, string>(attributes ?? []);
        if (fifo)
        {
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
        var name = FidelityContext.UniqueName("sim2real-behav-sns-q-") + (fifo ? ".fifo" : "");
        var attrs = new Dictionary<string, string>();
        if (fifo)
        {
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
        await PollUntil(
            async () => (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn })).Attributes?.GetValueOrDefault("PendingConfirmation"),
            pending => pending == "false",
            timeoutS: timeoutS, intervalS: 2.0,
            description: "the new subscription leaves PendingConfirmation");
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
                throw Fail($"{description}: not satisfied within {timeoutS}s, last value {Repr(value)}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
            value = await fn();
        }

        return value;
    }
}

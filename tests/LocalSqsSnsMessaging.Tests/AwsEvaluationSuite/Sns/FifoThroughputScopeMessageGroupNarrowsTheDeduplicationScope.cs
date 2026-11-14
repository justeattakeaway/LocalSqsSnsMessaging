// Translated from behavioral/sns/check_fifo_throughput_scope_message_group_narrows_the_deduplication_scope.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.fifo-throughput-scope-message-group-narrows-the-deduplication-scope</summary>
[Category("AwsEvaluationSuite")]
public sealed class FifoThroughputScopeMessageGroupNarrowsTheDeduplicationScope
{
    private const string Scenario = "sns-fifo-throughput-scope";

    /// <summary>
    /// Under FifoThroughputScope=Topic a reused dedup id in another message group is accepted but
    /// not delivered; under MessageGroup (topic and paired queue) both are delivered, and an in-group
    /// duplicate is still suppressed.
    ///
    /// Reference: https://docs.aws.amazon.com/sns/latest/dg/fifo-message-dedup.html
    /// </summary>
    [Test]
    [DisplayName("sns.fifo-throughput-scope-message-group-narrows-the-deduplication-scope")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsFifo, "Under FifoThroughputScope=Topic a reused MessageDeduplicationId in another message group is delivered instead of suppressed")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var sqs = ctx.Sqs;
        var topicArn = await CreateFifoTopic(ctx);
        var (queueUrl, queueArn) = await CreateFifoQueueForTopic(ctx, topicArn);
        await SubscribeAndConfirm(ctx, topicArn, queueArn,
            attributes: new() { ["RawMessageDelivery"] = "true" });

        var scope = (await sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = topicArn })).Attributes?.GetValueOrDefault("FifoThroughputScope");
        Ensure(scope == "Topic",
            "phase 1 requires FifoThroughputScope='Topic' on the topic this check created " +
            "(set explicitly at CreateTopic, since a fresh topic omits the attribute from " +
            "GetTopicAttributes entirely rather than defaulting it to a readable value); " +
            $"GetTopicAttributes read '{scope}'");

        const string groupA = "sim2real-group-a";
        const string groupB = "sim2real-group-b";

        // ---- phase 1: Topic scope — one dedup id spans both groups ----------------------------
        var dedup1 = FidelityContext.UniqueName("sim2real-dedup-");
        var firstWins = FidelityContext.UniqueName("sim2real-behav-fifoscope-topic-first-");
        var crossGroupTwin = FidelityContext.UniqueName("sim2real-behav-fifoscope-topic-twin-");

        var started = FidelityTime.UtcNow;
        await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = firstWins, MessageGroupId = groupA, MessageDeduplicationId = dedup1 });
        var accepted = await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = crossGroupTwin, MessageGroupId = groupB, MessageDeduplicationId = dedup1 });
        Ensure(!string.IsNullOrEmpty(accepted.MessageId),
            "a duplicate MessageDeduplicationId must be ACCEPTED (documented: 'accepted " +
            $"successfully but aren't delivered'), not refused; got {accepted}");

        var seen = await AwaitBodies(
            ctx, queueUrl, [firstWins], timeoutS: 90.0,
            description: "under Topic scope the FIRST message with the deduplication id must be " +
                         "delivered; without it the absence below would just mean broken wiring",
            forbidden: [crossGroupTwin]);
        var latency = Math.Max((seen[firstWins] - started).TotalSeconds, 1.0);
        var window = Math.Max(3.0 * latency, 15.0);
        var leaked = await Drain(ctx, queueUrl, windowS: window);
        Ensure(leaked.Count == 0,
            "under FifoThroughputScope=Topic a second publish reusing the deduplication id in a " +
            $"DIFFERENT message group must never be delivered; a {window.ToString("F0", CultureInfo.InvariantCulture)}s window (>=3x the " +
            $"measured latency of {latency.ToString("F1", CultureInfo.InvariantCulture)}s), begun after the first message arrived, produced " +
            $"{Repr(leaked)}");
        var approx = int.Parse((await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            AttributeNames = ["ApproximateNumberOfMessages"],
        })).Attributes!["ApproximateNumberOfMessages"], CultureInfo.InvariantCulture);
        Ensure(approx == 0,
            "second independent observable for the absence: expected ApproximateNumberOfMessages " +
            $"== 0 on the FIFO queue, got {approx}");

        // ---- phase 2: change exactly one attribute and DEMAND both deliveries ------------------
        //
        // The topic scope alone is not observable at a FIFO queue left at DeduplicationScope='queue':
        // SNS forwards its MessageDeduplicationId and the queue re-applies queue-wide dedup to it.
        // CreateFifoQueueForTopic pairs the queue to 'messageGroup' for that reason.
        await sns.SetTopicAttributesAsync(new SetTopicAttributesRequest
        {
            TopicArn = topicArn,
            AttributeName = "FifoThroughputScope",
            AttributeValue = "MessageGroup",
        });
        await PollUntil(
            async () => (await sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = topicArn })).Attributes?.GetValueOrDefault("FifoThroughputScope"),
            value => value == "MessageGroup",
            timeoutS: 60.0,
            intervalS: 2.0,
            description: "GetTopicAttributes reflects FifoThroughputScope=MessageGroup");

        var dedup2 = FidelityContext.UniqueName("sim2real-dedup-");
        var inGroupA = FidelityContext.UniqueName("sim2real-behav-fifoscope-group-a-");
        var inGroupB = FidelityContext.UniqueName("sim2real-behav-fifoscope-group-b-");
        await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = inGroupA, MessageGroupId = groupA, MessageDeduplicationId = dedup2 });
        await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = inGroupB, MessageGroupId = groupB, MessageDeduplicationId = dedup2 });

        // AwaitBodies raises unless BOTH arrive, and immediately if phase 1's suppressed twin
        // turns up late.
        await AwaitBodies(
            ctx, queueUrl, [inGroupA, inGroupB], timeoutS: 90.0,
            description: "under FifoThroughputScope=MessageGroup (topic AND queue paired) the SAME " +
                         "deduplication id in two different message groups must yield TWO " +
                         "deliveries — this is the beat that proves the attribute changed the " +
                         "behaviour rather than dedup being switched off or on wholesale",
            forbidden: [crossGroupTwin]);

        // A duplicate WITHIN one group must still be suppressed under MessageGroup scope: the
        // attribute narrows the scope, it does not disable deduplication.
        var sameGroupTwin = FidelityContext.UniqueName("sim2real-behav-fifoscope-same-group-twin-");
        await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = sameGroupTwin, MessageGroupId = groupA, MessageDeduplicationId = dedup2 });
        var residual = await Drain(ctx, queueUrl, windowS: window);
        Ensure(residual.Count == 0,
            "narrowing the scope to MessageGroup must not disable deduplication inside a group: " +
            $"reusing '{dedup2}' in '{groupA}' was delivered anyway ({Repr(residual)}) during a " +
            $"{window.ToString("F0", CultureInfo.InvariantCulture)}s window");
    }

    private static string Repr(IEnumerable<string> values) => $"[{string.Join(", ", values.Select(v => $"'{v}'"))}]";

    /// <summary>
    /// CreateTopic for a FIFO topic (name suffixed .fifo, FifoTopic=true), tagged, with
    /// DeleteTopic registered immediately.
    /// </summary>
    private static async Task<string> CreateFifoTopic(FidelityContext ctx)
    {
        var sns = ctx.Sns;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-t-") + ".fifo";
        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = name,
            // Set explicitly: a fresh FIFO topic omits FifoThroughputScope from GetTopicAttributes.
            Attributes = new()
            {
                ["FifoTopic"] = "true", ["ContentBasedDeduplication"] = "false",
                ["FifoThroughputScope"] = "Topic",
            },
            Tags = TagsKv(),
        })).TopicArn;
        ctx.OnCleanup(() => ctx.Sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>
    /// Create a real FIFO SQS queue whose policy lets exactly this topic publish to it.
    ///
    /// DeduplicationScope='messageGroup' is required: a queue-wide-dedup queue re-collapses the
    /// cross-group duplicate the topic's MessageGroup scope decided to deliver.
    /// </summary>
    private static async Task<(string Url, string Arn)> CreateFifoQueueForTopic(FidelityContext ctx, string topicArn)
    {
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-q-") + ".fifo";
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = name,
            Attributes = new()
            {
                ["FifoQueue"] = "true", ["DeduplicationScope"] = "messageGroup",
                ["FifoThroughputLimit"] = "perMessageGroupId",
            },
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

    private static List<Amazon.SimpleNotificationService.Model.Tag> TagsKv() =>
        [new() { Key = "sim2real", Value = Scenario }, new() { Key = "sim2real:managed", Value = "true" }];

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
            timeoutS: timeoutS,
            intervalS: 2.0,
            description: $"subscription {subArn} leaves PendingConfirmation");
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

    /// <summary>
    /// Long-poll until every wanted body is seen; raise on the deadline or on a forbidden body.
    ///
    /// Returns {body: time first seen} so the caller can calibrate an absence window.
    /// </summary>
    private static async Task<Dictionary<string, DateTimeOffset>> AwaitBodies(FidelityContext ctx, string queueUrl, HashSet<string> wanted,
        double timeoutS, string description, HashSet<string>? forbidden = null)
    {
        var sqs = ctx.Sqs;
        var seen = new Dictionary<string, DateTimeOffset>();
        var outstanding = new HashSet<string>(wanted);
        var deadline = FidelityTime.StartNew();
        while (outstanding.Count > 0)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                var body = msg.Body;
                if (forbidden is { Count: > 0 } && forbidden.Contains(body))
                {
                    throw Fail($"{description}: a body that must never be delivered arrived: '{body}'");
                }

                if (outstanding.Remove(body))
                {
                    seen[body] = FidelityTime.UtcNow;
                }
            }

            if (outstanding.Count > 0 && deadline.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw Fail($"{description}: {Repr(outstanding.Order(StringComparer.Ordinal))} never arrived within {timeoutS}s");
            }
        }

        return seen;
    }

    /// <summary>Long-poll for the whole window and return every body seen.</summary>
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
                bodies.Add(msg.Body);
            }
        }

        return bodies;
    }
}

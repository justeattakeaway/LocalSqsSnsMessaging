// Translated from behavioral/sns/check_publish_still_delivers_to_siblings_when_a_subscribed_queue_is_deleted.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.publish-still-delivers-to-siblings-when-a-subscribed-queue-is-deleted</summary>
[Category("AwsEvaluationSuite")]
public sealed class PublishStillDeliversToSiblingsWhenASubscribedQueueIsDeleted
{
    private const string Scenario = "sns-dead-endpoint-sibling";

    /// <summary>
    /// With one of two subscribed queues deleted, Publish still returns a MessageId, the surviving
    /// subscriber receives every message, and the dead subscription stays listed and confirmed:
    /// one failed endpoint neither fails Publish nor blocks fan-out to the others.
    /// </summary>
    [Test]
    [DisplayName("sns.publish-still-delivers-to-siblings-when-a-subscribed-queue-is-deleted")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var sqs = ctx.Sqs;
        var topicArn = await CreateTopic(ctx);

        var (liveUrl, liveArn) = await CreateQueueForTopic(ctx, topicArn);
        await SubscribeAndConfirm(ctx, topicArn, liveArn, attributes: new() { ["RawMessageDelivery"] = "true" });

        var deadName = FidelityContext.UniqueName("sim2real-behav-sns-deadq-");
        var deadUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = deadName,
            Tags = new() { ["sim2real"] = Scenario, ["sim2real:managed"] = "true" },
        })).QueueUrl;
        var deleted = false;
        ctx.OnCleanup(async () =>
        {
            if (!deleted)
            {
                await ctx.Sqs.DeleteQueueAsync(deadUrl);
            }
        });
        var deadArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = deadUrl, AttributeNames = ["QueueArn"] })).Attributes["QueueArn"];
        var deadSub = await SubscribeAndConfirm(ctx, topicArn, deadArn);

        await sqs.DeleteQueueAsync(deadUrl);
        deleted = true;

        async Task<string?> Resolves()
        {
            var error = await FidelityContext.CaptureError(() => sqs.GetQueueUrlAsync(new GetQueueUrlRequest { QueueName = deadName }));
            return error is null ? "still resolves" : error.Code;
        }

        await PollUntil(
            Resolves,
            code => code == "AWS.SimpleQueueService.NonExistentQueue",
            timeoutS: 90.0, intervalS: 3.0,
            description: "the deleted queue stops resolving");

        var marks = Enumerable.Range(1, 3).Select(i => FidelityContext.UniqueName($"sim2real-behav-deadq-m{i}-")).ToList();
        foreach (var marker in marks)
        {
            var resp = await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = marker });
            Ensure(!string.IsNullOrEmpty(resp.MessageId), $"Publish must succeed with a dead subscriber, got {{'MessageId': '{resp.MessageId}'}}");
        }

        await AwaitBodies(ctx, liveUrl, marks.ToHashSet(), timeoutS: 120.0,
            description: "the surviving subscriber receives every message");

        var attrs = (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = deadSub })).Attributes ?? [];
        Ensure(attrs.GetValueOrDefault("PendingConfirmation") == "false" && attrs.GetValueOrDefault("Endpoint") == deadArn,
            "the dead endpoint's subscription must remain confirmed and point at the deleted " +
            $"queue's ARN; PendingConfirmation='{attrs.GetValueOrDefault("PendingConfirmation")}', endpoint " +
            $"matches={(attrs.GetValueOrDefault("Endpoint") == deadArn ? "True" : "False")}");
        var listed = new List<bool>();
        await foreach (var page in sns.Paginators.ListSubscriptionsByTopic(new ListSubscriptionsByTopicRequest { TopicArn = topicArn }).Responses)
        {
            listed.AddRange((page.Subscriptions ?? []).Select(s => s.SubscriptionArn == deadSub));
        }

        listed.Sort();
        Ensure(listed.SequenceEqual([false, true]), $"the topic must still list both subscriptions, got {listed.Count} entries");
    }

    private static string Repr(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(s => $"'{s}'")) + "]";

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
    private static async Task<(string Url, string Arn)> CreateQueueForTopic(FidelityContext ctx, string topicArn, bool fifo = false,
        Dictionary<string, string>? attributes = null)
    {
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-q-") + (fifo ? ".fifo" : "");
        var attrs = new Dictionary<string, string>(attributes ?? []);
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
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes["QueueArn"];
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
            async () => (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn }))
                .Attributes?.GetValueOrDefault("PendingConfirmation"),
            pending => pending == "false",
            timeoutS: timeoutS, intervalS: 2.0,
            description: "the new subscription leaves PendingConfirmation");
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
    /// Long-poll until every wanted body is seen; RAISES on the deadline.
    /// Returns {body: monotonic time first seen}. A body in `forbidden` that arrives raises at
    /// once: it is a leak, and swallowing it would hide what the caller is about to assert.
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
}

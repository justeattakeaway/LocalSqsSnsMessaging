// Translated from behavioral/sns/check_fifo_deduplication_collapses_duplicate_publish.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.fifo-deduplication-collapses-duplicate-publish</summary>
[Category("AwsEvaluationSuite")]
public sealed class FifoDeduplicationCollapsesDuplicatePublish
{
    private const string Scenario = "sns-fifo-dedup-single";

    /// <summary>
    /// Republishing the same MessageDeduplicationId inside the dedup window is collapsed: the
    /// Publish response is identical, and the FIFO queue at
    /// the destination receives exactly one copy of the pair, not two.
    /// </summary>
    [Test]
    [DisplayName("sns.fifo-deduplication-collapses-duplicate-publish")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsFifo, "a duplicate Publish to a FIFO topic gets a new MessageId instead of the original's: the topic doesn't deduplicate")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var runId = FidelityContext.UniqueName("sim2real-behav-sns-fifodedup-");
        var topicArn = await CreateTopic(ctx, runId + ".fifo", attributes: new() { ["FifoTopic"] = "true" });
        var (queueUrl, queueArn) = await CreateQueueForTopic(ctx, topicArn, runId + "-q.fifo");
        await SubscribeAndConfirm(ctx, topicArn, queueArn);

        var dedupId = FidelityContext.UniqueName("sim2real-behav-dedup-");
        var first = await sns.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn, Message = "first-copy", MessageGroupId = "group-a", MessageDeduplicationId = dedupId,
        });
        var second = await sns.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn, Message = "second-copy", MessageGroupId = "group-a", MessageDeduplicationId = dedupId,
        });
        Ensure(second.MessageId == first.MessageId,
            "expected duplicate publish to return the original MessageId, got " +
            $"{first.MessageId} then {second.MessageId}");
        Ensure(second.SequenceNumber == first.SequenceNumber,
            "expected duplicate publish to return the original SequenceNumber, got " +
            $"{first.SequenceNumber} then {second.SequenceNumber}");
        var third = await sns.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn, Message = "third-copy", MessageGroupId = "group-a",
            MessageDeduplicationId = FidelityContext.UniqueName("sim2real-behav-dedup-other-"),
        });
        Ensure(third.MessageId != first.MessageId, "expected a distinct dedup id to produce a distinct message");
        Ensure(decimal.Parse(third.SequenceNumber ?? throw new KeyNotFoundException("'SequenceNumber'"), CultureInfo.InvariantCulture)
               > decimal.Parse(first.SequenceNumber ?? throw new KeyNotFoundException("'SequenceNumber'"), CultureInfo.InvariantCulture),
            "expected a distinct dedup id to advance the sequence number");

        const string sentinel = "sentinel-copy";
        await sns.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn, Message = sentinel, MessageGroupId = "group-a",
            MessageDeduplicationId = FidelityContext.UniqueName("sim2real-behav-dedup-sentinel-"),
        });
        var delivered = await DrainUntil(ctx, queueUrl, sentinel, timeoutS: 90.0);
        Ensure(delivered.SequenceEqual(["first-copy", "third-copy"]),
            "expected the FIFO queue to deliver exactly one copy of the deduplicated pair and " +
            $"the distinct message, in that order; got [{string.Join(", ", delivered.Select(d => $"'{d}'"))}]");
    }

    private static List<Amazon.SimpleNotificationService.Model.Tag> TagsKv() =>
        [new() { Key = "sim2real", Value = Scenario }, new() { Key = "sim2real:managed", Value = "true" }];

    /// <summary>CreateTopic (tagged) + register DeleteTopic cleanup; returns the TopicArn.</summary>
    private static async Task<string> CreateTopic(FidelityContext ctx, string name, Dictionary<string, string>? attributes = null)
    {
        var arn = (await ctx.Sns.CreateTopicAsync(new CreateTopicRequest { Name = name, Attributes = attributes ?? [], Tags = TagsKv() })).TopicArn;
        ctx.OnCleanup(() => ctx.Sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>Create a real FIFO SQS queue whose policy lets exactly this topic publish to it.</summary>
    private static async Task<(string Url, string Arn)> CreateQueueForTopic(FidelityContext ctx, string topicArn, string name)
    {
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = name,
            Attributes = new() { ["FifoQueue"] = "true" },
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

    /// <summary>Subscribe the queue and poll GetSubscriptionAttributes until it is confirmed.</summary>
    private static async Task<string> SubscribeAndConfirm(FidelityContext ctx, string topicArn, string queueArn, double timeoutS = 60.0)
    {
        var sns = ctx.Sns;
        var subArn = (await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn, Protocol = "sqs", Endpoint = queueArn, ReturnSubscriptionArn = true,
        })).SubscriptionArn;
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

    /// <summary>Unwrap the SNS Notification envelope and return its Message, or the body if it is not one.</summary>
    private static string InnerMessage(string body)
    {
        JsonNode? envelope;
        try
        {
            envelope = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return body;
        }

        if (envelope is JsonObject obj && obj["Type"] is JsonValue type && type.TryGetValue<string>(out var t) && t == "Notification"
            && obj["Message"] is JsonValue inner && inner.TryGetValue<string>(out var s))
        {
            return s;
        }

        return body;
    }

    /// <summary>Receive until <c>sentinel</c> arrives; returns everything seen BEFORE it, in order.</summary>
    private static async Task<List<string>> DrainUntil(FidelityContext ctx, string queueUrl, string sentinel, double timeoutS = 90.0)
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
                var inner = InnerMessage(msg.Body ?? "");
                if (inner == sentinel)
                {
                    return seen;
                }

                seen.Add(inner);
            }
        }

        throw Fail($"sentinel '{sentinel}' never arrived within {timeoutS}s; saw [{string.Join(", ", seen.Select(s => $"'{s}'"))}]");
    }
}

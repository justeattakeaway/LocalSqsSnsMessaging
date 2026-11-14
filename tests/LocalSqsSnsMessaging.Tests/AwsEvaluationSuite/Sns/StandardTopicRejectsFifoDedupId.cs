// Translated from behavioral/sns/check_standard_topic_rejects_fifo_dedup_id.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.standard-topic-rejects-fifo-dedup-id</summary>
[Category("AwsEvaluationSuite")]
public sealed class StandardTopicRejectsFifoDedupId
{
    private const string Scenario = "sns-standard-rejects-dedup-id";

    /// <summary>
    /// A standard topic rejects MessageDeduplicationId but accepts MessageGroupId without assigning
    /// a SequenceNumber; only the accepted publish reaches a subscribed SQS queue.
    /// </summary>
    [Test]
    [DisplayName("sns.standard-topic-rejects-fifo-dedup-id")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsValidation, "Publish to a standard topic accepts a MessageDeduplicationId instead of refusing it with InvalidParameter")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var sqs = ctx.Sqs;
        var topicArn = await CreateTopic(ctx);
        var (queueUrl, queueArn) = await CreateQueueForTopic(ctx, topicArn);
        await SubscribeAndConfirm(ctx, topicArn, queueArn, attributes: new() { ["RawMessageDelivery"] = "true" });

        var runId = FidelityContext.UniqueName("sim2real-behav-sns-stdfifo-");
        var acceptedMarker = $"{runId}-accepted";

        var (code, message) = await Error(() => sns.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn,
            Message = $"{runId}-rejected",
            MessageDeduplicationId = $"{runId}-dedup",
        }));
        Ensure(code == "InvalidParameter",
            $"expected InvalidParameter for MessageDeduplicationId on a standard topic, got '{code}'");
        Ensure(message == "Invalid parameter: MessageDeduplicationId Reason: The request includes MessageDeduplicationId parameter that is not valid for this topic type",
            $"unexpected refusal message for MessageDeduplicationId on a standard topic: '{message}'");

        // MessageGroupId, by contrast, is accepted on a standard topic.
        var resp = await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = acceptedMarker, MessageGroupId = $"{runId}-group" });
        Ensure(!string.IsNullOrEmpty(resp.MessageId),
            "real SNS accepts MessageGroupId on a standard topic; it must not be rejected");
        Ensure(resp.SequenceNumber is null,
            $"a standard topic must not return a SequenceNumber, got {resp}");

        var bodies = await PollUntilMessage(ctx, queueUrl, timeoutS: 60.0);
        Ensure(bodies.SequenceEqual([acceptedMarker]),
            $"expected exactly the accepted MessageGroupId publish to arrive at the subscriber, got [{string.Join(", ", bodies.Select(b => $"'{b}'"))}]");
        var extra = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 })).Messages ?? [];
        Ensure(extra.Count == 0,
            "the refused MessageDeduplicationId publish must never have been delivered, but " +
            $"unexpected message(s) arrived: [{string.Join(", ", extra.Select(m => $"'{m.Body}'"))}]");
    }

    /// <summary>
    /// The AWS error (Code, Message) raised by fn(). Raises if fn SUCCEEDS -- an unexpected success is
    /// the strongest possible signal.
    /// </summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"the call was expected to be rejected but SUCCEEDED, returning {result}");
        }

        return (error.Code, error.Message);
    }

    private static List<Amazon.SimpleNotificationService.Model.Tag> TagsKv() =>
        [new() { Key = "sim2real", Value = Scenario }, new() { Key = "sim2real:managed", Value = "true" }];

    /// <summary>
    /// CreateTopic (tagged + prefix-named, standard/non-FIFO) and register DeleteTopic; returns
    /// the ARN.
    /// </summary>
    private static async Task<string> CreateTopic(FidelityContext ctx)
    {
        var sns = ctx.Sns;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-stdfifo-");
        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest { Name = name, Tags = TagsKv() })).TopicArn;
        ctx.OnCleanup(() => ctx.Sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>Create a real SQS queue whose policy lets exactly this topic publish to it.</summary>
    private static async Task<(string Url, string Arn)> CreateQueueForTopic(FidelityContext ctx, string topicArn)
    {
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-stdfifo-q-");
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = name,
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

    /// <summary>
    /// Bounded long-poll retry for the queue's first batch of messages; raises on timeout rather
    /// than returning an empty/partial result.
    /// </summary>
    private static async Task<List<string>> PollUntilMessage(FidelityContext ctx, string queueUrl, double timeoutS = 60.0)
    {
        var sqs = ctx.Sqs;
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 10 });
            var messages = resp.Messages ?? [];
            if (messages.Count > 0)
            {
                foreach (var msg in messages)
                {
                    await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                }

                return messages.Select(m => m.Body ?? "").ToList();
            }
        }

        throw Fail($"no message arrived at '{queueUrl}' within {timeoutS}s");
    }
}

// Translated from behavioral/sns/check_fifo_publish_without_a_message_group_id_rejected_a_grouped_publish_delivers.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.fifo-publish-without-a-message-group-id-rejected-a-grouped-publish-delivers</summary>
[Category("AwsEvaluationSuite")]
public sealed class FifoPublishWithoutAMessageGroupIdRejectedAGroupedPublishDelivers
{
    private const string Scenario = "sns-fifo-group-required";

    /// <summary>
    /// Publish to a FIFO topic without MessageGroupId is rejected with InvalidParameter and nothing
    /// is delivered, while the identical publish carrying a MessageGroupId arrives in the FIFO queue.
    /// </summary>
    [Test]
    [DisplayName("sns.fifo-publish-without-a-message-group-id-rejected-a-grouped-publish-delivers")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsFifo, "Publish to a FIFO topic without MessageGroupId fails with an internal error (HTTP 500, no error code) instead of InvalidParameter 'The MessageGroupId parameter is required for FIFO topics'")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = await CreateTopic(ctx, fifo: true, attributes: new() { ["ContentBasedDeduplication"] = "false" });
        var (queueUrl, queueArn) = await CreateQueueForTopic(ctx, topicArn, fifo: true);
        await SubscribeAndConfirm(ctx, topicArn, queueArn, attributes: new() { ["RawMessageDelivery"] = "true" });

        var runId = FidelityContext.UniqueName("sim2real-behav-sns-grp-");
        var group = $"{runId}-group";
        var rejectedMarker = $"{runId}-rejected";
        var acceptedMarker = $"{runId}-accepted";

        // Keep MessageDeduplicationId so the refusal can only be about the missing group id. The AWS
        // code is InvalidParameter; InvalidParameterException is only the botocore class name.
        var (code, message) = await Error(() => sns.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn, Message = rejectedMarker, MessageDeduplicationId = $"{runId}-dedup-rejected",
        }));
        Ensure(code == "InvalidParameter", $"a FIFO publish without MessageGroupId was not rejected with InvalidParameter: '{code}'");
        Ensure(message == "Invalid parameter: The MessageGroupId parameter is required for FIFO topics",
            $"unexpected InvalidParameter message for FIFO publish missing MessageGroupId: '{message}'");

        await sns.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn, Message = acceptedMarker, MessageGroupId = group, MessageDeduplicationId = $"{runId}-dedup-accepted",
        });
        // FIFO order within the group: once the sentinel lands, every earlier publish has too.
        var sentinel = $"{runId}-sentinel";
        await sns.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn, Message = sentinel, MessageGroupId = group, MessageDeduplicationId = $"{runId}-dedup-sentinel",
        });
        var bodies = await DrainUntilBody(ctx, queueUrl, sentinel, timeoutS: 120.0);
        bodies.AddRange(await ReceiveAny(ctx, queueUrl, rounds: 1, waitS: 20));

        Ensure(bodies.Count == 1, $"expected exactly one delivery, got {bodies.Count}: {Repr(bodies)}");
        Ensure(bodies.SequenceEqual([acceptedMarker]), $"the queue holds something other than exactly the accepted message: {Repr(bodies)}");
        Ensure(!bodies.Contains(rejectedMarker), $"the refused publish was delivered anyway: {Repr(bodies)}");
    }

    private static string Repr(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(i => $"'{i}'")) + "]";

    /// <summary>The AWS error (Code, Message) raised by fn(); raises if fn succeeds.</summary>
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

    /// <summary>CreateTopic (tagged + prefix-named) and register DeleteTopic immediately; returns the ARN.</summary>
    private static async Task<string> CreateTopic(FidelityContext ctx, bool fifo = false, Dictionary<string, string>? attributes = null)
    {
        var name = FidelityContext.UniqueName("sim2real-behav-sns-t-");
        var attrs = new Dictionary<string, string>(attributes ?? []);
        if (fifo)
        {
            name += ".fifo";
            attrs["FifoTopic"] = "true";
        }

        var arn = (await ctx.Sns.CreateTopicAsync(new CreateTopicRequest { Name = name, Attributes = attrs, Tags = TagsKv() })).TopicArn;
        ctx.OnCleanup(() => ctx.Sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>Create a real SQS queue whose policy lets exactly this topic publish to it.</summary>
    private static async Task<(string Url, string Arn)> CreateQueueForTopic(FidelityContext ctx, string topicArn, bool fifo = false)
    {
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-q-");
        var attrs = new Dictionary<string, string>();
        if (fifo)
        {
            name += ".fifo";
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

    /// <summary>Receive until the sentinel body arrives; returns everything seen BEFORE it, in order.</summary>
    private static async Task<List<string>> DrainUntilBody(FidelityContext ctx, string queueUrl, string sentinel, double timeoutS = 120.0)
    {
        var sqs = ctx.Sqs;
        var before = new List<string>();
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                var body = msg.Body ?? "";
                if (body == sentinel)
                {
                    return before;
                }

                before.Add(body);
            }
        }

        throw Fail($"the FIFO sentinel '{sentinel}' never arrived within {timeoutS}s, so the absence of the " +
            $"refused publish is not bounded; messages seen were {Repr(before)}");
    }

    /// <summary>Long-poll a queue <c>rounds</c> times and return every body it produced (expected: none).</summary>
    private static async Task<List<string>> ReceiveAny(FidelityContext ctx, string queueUrl, int rounds, int waitS)
    {
        var sqs = ctx.Sqs;
        var bodies = new List<string>();
        for (var i = 0; i < rounds; i++)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = waitS });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                bodies.Add(msg.Body ?? "");
            }
        }

        return bodies;
    }
}

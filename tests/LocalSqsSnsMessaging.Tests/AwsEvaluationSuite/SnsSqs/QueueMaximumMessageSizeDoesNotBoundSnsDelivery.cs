// Translated from behavioral/sns__sqs/check_queue_maximum_message_size_does_not_bound_sns_delivery.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.SnsSqs;

/// <summary>sns__sqs.queue-maximum-message-size-does-not-bound-sns-delivery</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueueMaximumMessageSizeDoesNotBoundSnsDelivery
{
    private const string Check = "queue-maximum-message-size-does-not-bound-sns-delivery";

    private const string OversizeSendCode = "InvalidParameterValue";
    private const string OversizeSendMessage = "One or more parameters are invalid. Reason: Message must be shorter than 1024 bytes.";

    /// <summary>Return (Error.Code, Error.Message) of the ClientError fn raises; a success raises.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }

    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };

    /// <summary>
    /// A topic delivers a message larger than its queue subscriber's own MaximumMessageSize
    /// intact, while SendMessage to that queue with the same body is refused by that attribute.
    /// </summary>
    [Test]
    [DisplayName("sns__sqs.queue-maximum-message-size-does-not-bound-sns-delivery")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var sqs = ctx.Sqs;

        var topicArn = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = FidelityContext.UniqueName("sim2real-behav-sns-maxmsg-t-"),
            Tags = [new() { Key = "sim2real", Value = Check }],
        })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = topicArn }));

        var queueUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-sns-maxmsg-q-"),
            Attributes = new() { ["MaximumMessageSize"] = "1024" },
            Tags = new() { ["sim2real"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(Teardown(() => sqs.DeleteQueueAsync(queueUrl), "AWS.SimpleQueueService.NonExistentQueue"));

        var queueAttrs = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            AttributeNames = ["QueueArn", "MaximumMessageSize"],
        })).Attributes!;
        var queueArn = queueAttrs["QueueArn"];
        Ensure(queueAttrs["MaximumMessageSize"] == "1024",
            $"positive control: expected the queue's own MaximumMessageSize to read back as " +
            $"'1024', got '{queueAttrs["MaximumMessageSize"]}'");

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

        var subArn = (await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "sqs",
            Endpoint = queueArn,
            Attributes = new() { ["RawMessageDelivery"] = "true" },
            ReturnSubscriptionArn = true,
        })).SubscriptionArn;
        ctx.OnCleanup(() => sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subArn }));

        var deadline = FidelityTime.StartNew();
        var pending = (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn })).Attributes?.GetValueOrDefault("PendingConfirmation");
        while (pending != "false")
        {
            if (deadline.Elapsed >= TimeSpan.FromSeconds(60.0))
            {
                throw Fail($"subscription never left PendingConfirmation; last value {(pending is null ? "None" : $"'{pending}'")}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1.0));
            pending = (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn })).Attributes?.GetValueOrDefault("PendingConfirmation");
        }

        var marker = FidelityContext.UniqueName("sim2real-behav-sns-maxmsg-body-");
        var body = marker + new string('x', 4096 - marker.Length);
        Ensure(body.Length == 4096, $"test body must be exactly 4096 bytes, built {body.Length}");

        // --- break: the queue's own MaximumMessageSize refuses a direct SendMessage of this body ---
        var (code, message) = await Error(() => sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = queueUrl, MessageBody = body }));
        Ensure(code == OversizeSendCode && message == OversizeSendMessage,
            $"expected SendMessage to be refused with ('{OversizeSendCode}', '{OversizeSendMessage}'), got ('{code}', '{message}')");
        var rejected = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 })).Messages ?? [];
        Ensure(rejected.Count == 0, $"the refused SendMessage enqueued a message anyway: [{string.Join(", ", rejected.Select(m => $"'{m.Body}'"))}]");

        // --- the topic delivers the same oversize body to the same queue intact ---
        var publishResp = await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = body });
        Ensure(!string.IsNullOrEmpty(publishResp.MessageId), $"expected a MessageId for the publish, got {publishResp}");

        deadline = FidelityTime.StartNew();
        Message? delivered = null;
        while (delivered is null)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                if (msg.Body == body)
                {
                    delivered = msg;
                }

                // else: an unrelated message on a shared queue would be a defect in this check's
                // isolation, not something to ignore; the queue is private to this check.
            }

            if (delivered is null && deadline.Elapsed >= TimeSpan.FromSeconds(90.0))
            {
                throw Fail("the SNS-delivered message carrying the 4096-byte body never arrived within 90s");
            }
        }

        Ensure(delivered.Body == body,
            $"the message delivered via SNS must carry the exact 4096-byte body; got " +
            $"{delivered.Body.Length} bytes");
    }
}

// Translated from behavioral/sns/check_publish_without_target_fails.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.publish-without-target-fails</summary>
[Category("AwsEvaluationSuite")]
public sealed class PublishWithoutTargetFails
{
    private const string Scenario = "sns-publish-no-target";

    /// <summary>
    /// Publish with no TopicArn/TargetArn/PhoneNumber is rejected with the specific validation
    /// error, while an otherwise-identical publish that DOES carry a TopicArn is delivered — the
    /// positive control that separates "rejects a targetless publish" from "rejects every publish".
    /// </summary>
    [Test]
    [DisplayName("sns.publish-without-target-fails")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var (topicArn, queueUrl) = await CreateWiredTopic(ctx);

        var (code, message) = await Error(() => sns.PublishAsync(new PublishRequest { Message = "no target specified" }));
        Ensure(code == "InvalidParameter", $"expected InvalidParameter, got '{code}'");
        Ensure(message == "Invalid parameter: TopicArn or TargetArn Reason: no value for required parameter",
            $"expected the exact message 'Invalid parameter: TopicArn or TargetArn Reason: no value for required parameter', got '{message}'");

        var marker = FidelityContext.UniqueName("sim2real-behav-sns-notarget-msg-");
        await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = marker });
        var bodies = await ReceiveUntil(ctx, queueUrl, marker);
        Ensure(bodies.Contains(marker), $"the positive-control publish never arrived at the queue: {Repr(bodies)}");
    }

    private static string Repr(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(i => $"'{i}'")) + "]";

    /// <summary>
    /// The AWS error (Code, Message) raised by fn(). Raises if fn SUCCEEDS — an unexpected success is
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

    /// <summary>Create a topic with a confirmed, raw-delivery SQS subscription; returns (TopicArn, QueueUrl).</summary>
    private static async Task<(string TopicArn, string QueueUrl)> CreateWiredTopic(FidelityContext ctx)
    {
        var sns = ctx.Sns;
        var sqs = ctx.Sqs;

        var topicArn = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = FidelityContext.UniqueName("sim2real-behav-sns-notarget-"), Tags = TagsKv(),
        })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = topicArn }));

        var queueUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-sns-notarget-q-"),
            Tags = new() { ["sim2real"] = Scenario, ["sim2real:managed"] = "true" },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(queueUrl));
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

        var subArn = (await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn, Protocol = "sqs", Endpoint = queueArn, ReturnSubscriptionArn = true,
            Attributes = new() { ["RawMessageDelivery"] = "true" },
        })).SubscriptionArn;
        ctx.OnCleanup(() => sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subArn }));

        var deadline = FidelityTime.StartNew();
        string? pending = null;
        var confirmed = false;
        while (deadline.Elapsed < TimeSpan.FromSeconds(60.0))
        {
            pending = (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn }))
                .Attributes?.GetValueOrDefault("PendingConfirmation");
            if (pending == "false")
            {
                confirmed = true;
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1.0));
        }

        if (!confirmed)
        {
            throw Fail($"subscription '{subArn}' still PendingConfirmation='{pending}'");
        }

        return (topicArn, queueUrl);
    }

    /// <summary>Long-poll the queue until <c>marker</c> arrives; returns every body seen along the way.</summary>
    private static async Task<List<string>> ReceiveUntil(FidelityContext ctx, string queueUrl, string marker, double timeoutS = 60.0)
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
                seen.Add(msg.Body ?? "");
            }

            if (seen.Contains(marker))
            {
                return seen;
            }
        }

        throw Fail($"'{marker}' never arrived at the queue within {timeoutS}s; saw {Repr(seen)}");
    }
}

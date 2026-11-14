// Translated from behavioral/sqs/check_message_retention_period_expires_undeleted_message.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.message-retention-period-expires-undeleted-message</summary>
[Category("AwsEvaluationSuite")]
public sealed class MessageRetentionPeriodExpiresUndeletedMessage
{
    // On real SQS ApproximateNumberOfMessages can read 0 while ReceiveMessage still returns the expired
    // message, so receivability is polled first and the counter asserted afterwards.
    // 240s bounds the poll; the assertion is on the eventual state, not on a deadline.
    private const int ExpiryCeilingSeconds = 240;
    private const int EmptyPolls = 3;

    private static Dictionary<string, string> Tags => new()
    {
        ["sim2real:managed"] = "true",
        ["sim2real"] = "message-retention-period-expiry",
    };

    private static async Task<T> Poll<T>(Func<Task<T>> fn, Func<T, bool> pred, double timeoutSeconds, double intervalSeconds = 2.0)
    {
        var deadline = Stopwatch.StartNew();
        var val = await fn();
        while (!pred(val))
        {
            if (deadline.Elapsed >= TimeSpan.FromSeconds(timeoutSeconds))
            {
                return val;
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds));
            val = await fn();
        }

        return val;
    }

    private static async Task<string> Depth(AmazonSQSClient sqs, string url)
        => (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = url,
            AttributeNames = ["ApproximateNumberOfMessages"],
        })).Attributes!.GetValueOrDefault("ApproximateNumberOfMessages", "?");

    private static string Show(IEnumerable<string> values) => "[" + string.Join(", ", values.Select(v => $"'{v}'")) + "]";

    /// <summary>
    /// A message on a MessageRetentionPeriod=60 queue becomes unreceivable once retention elapses,
    /// with no delete, while the same message on a default four-day retention queue is still there.
    /// </summary>
    // timing:real-time — retention: 60 s https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_SetQueueAttributes.html
    [Test]
    [DisplayName("sqs.message-retention-period-expires-undeleted-message")]
    [Timeout(FidelityContext.RealTimeBudgetMilliseconds)]
    [Category(ApplyDefaultCategoryAttribute.TimeBasedCategoryName)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.RealTimeBudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var marker = $"retention-marker-{Guid.NewGuid().ToString("N")[..8]}";

        // 60 is the API MINIMUM for MessageRetentionPeriod; the window cannot be made shorter.
        var urlShort = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-"),
            Attributes = new() { ["MessageRetentionPeriod"] = "60" },
            Tags = Tags,
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(urlShort));

        var urlLong = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-"),
            Tags = Tags,
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(urlLong));

        var shortAttrs = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = urlShort, AttributeNames = ["All"] })).Attributes!;
        Ensure(shortAttrs.GetValueOrDefault("MessageRetentionPeriod") == "60",
            $"the short queue did not take the 60s retention, so nothing below is an expiry test: " +
            $"MessageRetentionPeriod='{shortAttrs.GetValueOrDefault("MessageRetentionPeriod")}'");
        var longAttrs = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = urlLong, AttributeNames = ["All"] })).Attributes!;
        Ensure(longAttrs.GetValueOrDefault("MessageRetentionPeriod") == "345600",
            $"the control queue must carry the DEFAULT four-day retention or it is not a control: " +
            $"MessageRetentionPeriod='{longAttrs.GetValueOrDefault("MessageRetentionPeriod")}'");

        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = urlShort, MessageBody = marker });
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = urlLong, MessageBody = marker });

        // Prove the message landed and was receivable first. Retention runs off SentTimestamp, so
        // receives at VisibilityTimeout=0 neither hold the message nor change the expiry.
        var preDepth = await Poll(() => Depth(sqs, urlShort), v => v == "1", timeoutSeconds: 60);
        Ensure(preDepth == "1",
            $"the message never landed on the short-retention queue, so nothing below proves " +
            $"retention: ApproximateNumberOfMessages='{preDepth}'");
        List<Message> preMsgs = [];
        var preDeadline = Stopwatch.StartNew();
        while (preMsgs.Count == 0 && preDeadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            preMsgs = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = urlShort,
                MaxNumberOfMessages = 1,
                WaitTimeSeconds = 2,
                VisibilityTimeout = 0,
            })).Messages ?? [];
        }

        Ensure(preMsgs.Select(m => m.Body).SequenceEqual([marker]),
            $"the message was counted but never receivable, so the disappearance below is not an " +
            $"expiry: got {Show(preMsgs.Select(m => m.Body))}, expected {Show([marker])}");

        // Poll receivability, not the counter (see _EXPIRY_CEILING_S). MaxNumberOfMessages=1: with
        // VisibilityTimeout=0 and a batch of 10, real SQS can return the same message ten times.
        List<string> lastBodies = [marker];
        var empties = 0;
        var expiryDeadline = Stopwatch.StartNew();
        while (empties < EmptyPolls && expiryDeadline.Elapsed < TimeSpan.FromSeconds(ExpiryCeilingSeconds))
        {
            lastBodies = [.. ((await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = urlShort,
                MaxNumberOfMessages = 1,
                WaitTimeSeconds = 2,
                VisibilityTimeout = 0,
            })).Messages ?? []).Select(m => m.Body)];
            empties = lastBodies.Count == 0 ? empties + 1 : 0;
        }

        Ensure(empties >= EmptyPolls,
            $"a message past its 60s MessageRetentionPeriod was still being delivered after " +
            $"{ExpiryCeilingSeconds}s: the last poll returned {Show(lastBodies)}. Retention expiry did " +
            $"not run");

        var shortDepthAfter = await Poll(() => Depth(sqs, urlShort), v => v == "0", timeoutSeconds: 60);
        Ensure(shortDepthAfter == "0",
            $"the message stopped being receivable but ApproximateNumberOfMessages did not return to " +
            $"0: '{shortDepthAfter}' -- the queue still counts a message it will never deliver");

        // The control, read last, attributes the disappearance to retention.
        var longMsgs = new List<Message>();
        for (var i = 0; i < 3; i++)
        {
            longMsgs.AddRange((await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = urlLong,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 5,
            })).Messages ?? []);
            if (longMsgs.Count > 0)
            {
                break;
            }
        }

        Ensure(longMsgs.Select(m => m.Body).SequenceEqual([marker]),
            $"the control queue with the default four-day retention lost its message too, so the " +
            $"disappearance above was not retention: {Show(longMsgs.Select(m => m.Body))}");
    }
}

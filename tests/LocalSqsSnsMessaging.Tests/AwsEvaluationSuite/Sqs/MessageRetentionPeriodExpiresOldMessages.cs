// Translated from behavioral/sqs/check_message_retention_period_expires_old_messages.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.message-retention-period-expires-old-messages</summary>
[Category("AwsEvaluationSuite")]
public sealed class MessageRetentionPeriodExpiresOldMessages
{
    /// <summary>
    /// A message outlives its queue's MessageRetentionPeriod and is permanently discarded (never delivered again).
    /// </summary>
    // timing:real-time — retention: 60 s https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_SetQueueAttributes.html
    [Test]
    [DisplayName("sqs.message-retention-period-expires-old-messages")]
    [Timeout(FidelityContext.RealTimeBudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.Other, "message was not expired by the retention period within the expected window (last consecutive_clean=0)")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.RealTimeBudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        // MessageRetentionPeriod=60 is the documented minimum; set at CREATE time so it applies
        // immediately (changing it later on an existing queue can take up to 15 minutes to
        // propagate per AWS docs, which this check avoids).
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-"),
            Attributes = new() { ["MessageRetentionPeriod"] = "60" },
            Tags = new() { ["sim2real:managed"] = "true", ["sim2real"] = "message-retention-period-expiry" },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var sentAt = Stopwatch.StartNew();
        var messageId = (await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "ephemeral-message" })).MessageId;

        // Confirm it is initially receivable (sanity, not the assertion under test).
        var initial = await PollUntil(
            async () => (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 5 })).Messages ?? [],
            msgs => msgs.Any(m => m.MessageId == messageId),
            timeoutSeconds: 15.0);
        var handle = initial.First(m => m.MessageId == messageId).ReceiptHandle;
        // Immediately release it back to the queue so it can expire from a "stored" state.
        await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest { QueueUrl = url, ReceiptHandle = handle, VisibilityTimeout = 0 });

        // Poll (bounded, generously past the 60s retention window) for the message to be gone for good.
        // A single empty ReceiveMessage is not proof of permanent absence -- SQS is a distributed queue
        // and can transiently return no messages for one that is still there. Require several
        // consecutive clean reads (empty receive AND both counters at zero) before concluding expiry,
        // the same freeze-detector guard used elsewhere against this exact flake.
        const int ConsecutiveCleanRequired = 3;
        var consecutiveClean = 0;
        var deadline = TimeSpan.FromSeconds(160.0);
        while (sentAt.Elapsed < deadline)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 3 });
            if ((resp.Messages ?? []).Any(m => m.MessageId == messageId))
            {
                consecutiveClean = 0;
                await Task.Delay(TimeSpan.FromSeconds(2));
                continue;
            }

            var attrs = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = url,
                AttributeNames = ["ApproximateNumberOfMessages", "ApproximateNumberOfMessagesNotVisible"],
            })).Attributes!;
            if (attrs["ApproximateNumberOfMessages"] == "0" && attrs["ApproximateNumberOfMessagesNotVisible"] == "0")
            {
                consecutiveClean++;
                if (consecutiveClean >= ConsecutiveCleanRequired)
                {
                    return;
                }
            }
            else
            {
                consecutiveClean = 0;
            }

            await Task.Delay(TimeSpan.FromSeconds(3));
        }

        throw Fail(
            $"message was not expired by the retention period within the expected window " +
            $"(last consecutive_clean={consecutiveClean})");
    }

    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutSeconds = 30.0, double intervalSeconds = 1.0)
    {
        var deadline = Stopwatch.StartNew();
        T? last = default;
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"condition not met within {timeoutSeconds}s (last={last})");
    }
}

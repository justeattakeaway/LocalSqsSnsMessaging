// Translated from behavioral/sqs/check_receive_message_visibility_timeout_override_per_call.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.receive-message-visibility-timeout-override-per-call</summary>
[Category("AwsEvaluationSuite")]
public sealed class ReceiveMessageVisibilityTimeoutOverridePerCall
{
    /// <summary>
    /// ReceiveMessage's own VisibilityTimeout parameter overrides the queue's default for that receipt.
    /// </summary>
    [Test]
    [DisplayName("sqs.receive-message-visibility-timeout-override-per-call")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-"),
            Attributes = new() { ["VisibilityTimeout"] = "60" },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var messageId = (await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "override-my-visibility" })).MessageId;

        // Override the 60s queue default down to 3s for this one receipt.
        var received = (await PollUntil(
            async () => (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url,
                MaxNumberOfMessages = 1,
                WaitTimeSeconds = 5,
                VisibilityTimeout = 3,
            })).Messages ?? [],
            msgs => msgs.Count == 1,
            timeoutS: 20.0))[0];
        Ensure(received.MessageId == messageId,
            $"expected to receive '{messageId}', got '{received.MessageId}'");

        // The override must actually HIDE the message -- an independent read taken right after
        // receipt must not see it. Without this, a target that ignores VisibilityTimeout entirely
        // (always returns the message) would still pass the reappearance check below.
        var stillHidden = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = url,
            MaxNumberOfMessages = 10,
            WaitTimeSeconds = 0,
        })).Messages ?? [];
        Ensure(!stillHidden.Any(m => m.MessageId == messageId),
            $"expected {messageId} hidden immediately after a 3s VisibilityTimeout receipt, "
            + $"got it back in [{string.Join(", ", stillHidden.Select(m => m.MessageId))}]");

        // Well before the queue's 60s default would elapse, the per-call 3s override must have expired.
        var reappeared = await PollUntil(
            async () => (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 2 })).Messages ?? [],
            msgs => msgs.Any(m => m.MessageId == messageId),
            timeoutS: 15.0);
        var msg = reappeared.First(m => m.MessageId == messageId);
        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle });
    }

    private static async Task<List<Message>> PollUntil(Func<Task<List<Message>>> probe, Func<List<Message>, bool> predicate, double timeoutS = 30.0, double intervalS = 1.0)
    {
        var deadline = Stopwatch.StartNew();
        List<Message>? last = null;
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"condition not met within {timeoutS}s (last={(last is null ? "None" : $"{last.Count} message(s)")})");
    }
}

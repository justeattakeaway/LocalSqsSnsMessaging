// Translated from behavioral/sqs/check_long_poll_returns_immediately_when_message_already_available.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.long-poll-returns-immediately-when-message-already-available</summary>
[Category("AwsEvaluationSuite")]
public sealed class LongPollReturnsImmediatelyWhenMessageAlreadyAvailable
{
    /// <summary>
    /// A long-poll ReceiveMessage call returns right away when a message is already sitting on the queue.
    /// </summary>
    [Test]
    [DisplayName("sqs.long-poll-returns-immediately-when-message-already-available")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var tags = new Dictionary<string, string>
        {
            ["sim2real:managed"] = "true",
            ["sim2real"] = "long-poll-returns-immediately-when-message-already-available",
        };
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-"), Tags = tags })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var messageId = await PollUntil(
            async () => (await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "already-here" })).MessageId,
            mid => !string.IsNullOrEmpty(mid),
            timeoutS: 10.0);
        // Give the message a moment to actually be visible for retrieval before we time the long poll.
        await PollUntil(
            async () => (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = url, AttributeNames = ["ApproximateNumberOfMessages"],
            })).Attributes!["ApproximateNumberOfMessages"],
            n => n != "0",
            timeoutS: 15.0);

        var start = Stopwatch.StartNew();
        var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 18 });
        var elapsed = start.Elapsed.TotalSeconds;

        var messages = resp.Messages ?? [];
        Ensure(messages.Any(m => m.MessageId == messageId),
            $"expected message '{messageId}' among returned messages, got " +
            $"[{string.Join(", ", messages.Select(m => m.MessageId))}]");
        // elapsed-bound: a long poll returns as soon as a message is available, not at WaitTimeSeconds.
        Ensure(elapsed < 10, $"long poll took {elapsed:F1}s for an already-available message (WaitTimeSeconds=18)");

        foreach (var m in messages)
        {
            await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle });
        }
    }

    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutS = 30.0, double intervalS = 1.0)
    {
        var deadline = Stopwatch.StartNew();
        T? last = default;
        while (deadline.Elapsed.TotalSeconds < timeoutS)
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"condition not met within {timeoutS}s (last='{last}')");
    }
}

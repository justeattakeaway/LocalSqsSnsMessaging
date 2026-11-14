// Translated from behavioral/sqs/check_approximate_receive_count_increments.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.approximate-receive-count-increments</summary>
[Category("AwsEvaluationSuite")]
public sealed class ApproximateReceiveCountIncrements
{
    private const string Check = "approximate-receive-count-increments";

    /// <summary>
    /// ApproximateReceiveCount increments each time a message is redelivered after visibility expiry.
    /// </summary>
    [Test]
    [DisplayName("sqs.approximate-receive-count-increments")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-"),
            Attributes = new() { ["VisibilityTimeout"] = "2" },
            Tags = new() { ["sim2real-check"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var messageId = (await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "track-my-receives" })).MessageId;

#pragma warning disable CS0618 // The check deliberately uses the legacy AttributeNames parameter.
        var first = (await PollUntil(
            async () => (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 5, AttributeNames = ["ApproximateReceiveCount"],
            })).Messages ?? [],
            msgs => msgs.Count == 1,
            timeoutS: 20.0))[0];
        Ensure(first.MessageId == messageId, $"expected first receive to return '{messageId}', got '{first.MessageId}'");
        Ensure(first.Attributes!["ApproximateReceiveCount"] == "1",
            $"expected ApproximateReceiveCount=1 on first receive, got '{first.Attributes!["ApproximateReceiveCount"]}'");

        var second = await PollUntil(
            async () => (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 3, AttributeNames = ["ApproximateReceiveCount"],
            })).Messages ?? [],
            msgs => msgs.Any(m => m.MessageId == messageId),
            timeoutS: 20.0);
#pragma warning restore CS0618
        var reborn = second.First(m => m.MessageId == messageId);
        Ensure(reborn.Attributes!["ApproximateReceiveCount"] == "2",
            $"expected ApproximateReceiveCount=2 after redelivery past the 2s VisibilityTimeout, got '{reborn.Attributes!["ApproximateReceiveCount"]}'");
        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = reborn.ReceiptHandle });
    }

    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutS = 30.0, double intervalS = 1.0)
    {
        var deadline = Stopwatch.StartNew();
        T? last = default;
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"condition not met within {timeoutS}s (last={last})");
    }
}

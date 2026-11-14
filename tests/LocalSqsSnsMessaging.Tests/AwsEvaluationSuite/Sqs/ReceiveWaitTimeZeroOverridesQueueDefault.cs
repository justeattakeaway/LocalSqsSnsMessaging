// Translated from behavioral/sqs/check_receive_wait_time_zero_overrides_queue_default.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.Runtime;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.receive-wait-time-zero-overrides-queue-default</summary>
[Category("AwsEvaluationSuite")]
public sealed class ReceiveWaitTimeZeroOverridesQueueDefault
{
    private const string Check = "receive-wait-time-zero-overrides-queue-default";

    /// <summary>
    /// ReceiveMessage(WaitTimeSeconds=0) on an empty queue whose ReceiveMessageWaitTimeSeconds is 20
    /// is a short poll: it answers empty and does not pick up a message sent after it went out.
    ///
    /// Reference: https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_ReceiveMessage.html
    /// </summary>
    [Test]
    [DisplayName("sqs.receive-wait-time-zero-overrides-queue-default")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-rwz-"),
            Attributes = new() { ["ReceiveMessageWaitTimeSeconds"] = "20" },
            Tags = new() { ["sim2real"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Fire when the ReceiveMessage request goes on the wire, so the send below follows it.
        // (botocore's "before-send.sqs.ReceiveMessage" hook; the .NET SDK's nearest is BeforeRequestEvent.)
        sqs.BeforeRequestEvent += (_, e) =>
        {
            if (e is WebServiceRequestEventArgs { Request: ReceiveMessageRequest })
            {
                dispatched.TrySetResult();
            }
        };

        var future = Task.Run(() => sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = url,
            MaxNumberOfMessages = 1,
            WaitTimeSeconds = 0,
        }));
        Ensure(await Completes(dispatched.Task, BudgetS(ctx, 10)),
            "the ReceiveMessage request was never dispatched");
        // A short poll has answered by now; one that fell back to the queue's 20 s long poll is
        // still open and returns the message sent next.
        await Completes(future, BudgetS(ctx, 10));
        const string body = "sent-after-the-short-poll";
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = body });
        var messages = (await future.WaitAsync(TimeSpan.FromSeconds(BudgetS(ctx, 40)))).Messages ?? [];

        Ensure(messages.Count == 0,
            $"ReceiveMessage(WaitTimeSeconds=0) returned [{string.Join(", ", messages.Select(m => m.Body))}]: it waited on the queue's 20 s "
            + "ReceiveMessageWaitTimeSeconds instead of answering as a short poll");

        // Positive control: the message sent after the short poll is on the queue.
        List<Message> received = [];
        var deadline = Stopwatch.StartNew();
        var budget = BudgetS(ctx, 30);
        while (received.Count == 0 && deadline.Elapsed < TimeSpan.FromSeconds(budget))
        {
            received = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url,
                MaxNumberOfMessages = 1,
                WaitTimeSeconds = 2,
            })).Messages ?? [];
        }

        if (received.Count == 0)
        {
            throw new TimeoutException($"the message '{body}' was never received from the queue");
        }

        Ensure(received[0].Body == body, $"unexpected body: '{received[0].Body}'");
    }

    /// <summary>A wait's timeout, bounded by the runner's remaining time so the wait fails with its own message.</summary>
    private static double BudgetS(FidelityContext ctx, double capS)
    {
        var left = ctx.TimeLeftSeconds;
        return Math.Max(1.0, Math.Min(capS, left - 5.0));
    }

    /// <summary>Waits up to <paramref name="timeoutS"/> for <paramref name="task"/>; true when it completed.</summary>
    private static async Task<bool> Completes(Task task, double timeoutS)
    {
        var winner = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(timeoutS)));
        return winner == task;
    }
}

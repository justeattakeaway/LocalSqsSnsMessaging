// Translated from behavioral/sqs/check_queue_level_wait_time_wakes_on_arrival.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.Runtime;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.queue-level-wait-time-wakes-on-arrival</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueueLevelWaitTimeWakesOnArrival
{
    private const string Check = "queue-level-wait-time-wakes-on-arrival";

    /// <summary>
    /// A ReceiveMessage call with no WaitTimeSeconds inherits the queue's
    /// ReceiveMessageWaitTimeSeconds=20: it stays open on an empty queue and returns a message sent
    /// while it waits.
    ///
    /// Reference: https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/sqs-short-and-long-polling.html
    /// </summary>
    [Test]
    [DisplayName("sqs.queue-level-wait-time-wakes-on-arrival")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-qlwt-"),
            Attributes = new() { ["ReceiveMessageWaitTimeSeconds"] = "20" },
            Tags = new() { ["sim2real"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Fire when the ReceiveMessage request actually goes on the wire, so the send below
        // is guaranteed to land while the poll is open rather than before it starts.
        // (botocore's before-send.sqs.ReceiveMessage hook -> the .NET SDK's BeforeRequestEvent.)
        sqs.BeforeRequestEvent += (_, e) =>
        {
            if (e is WebServiceRequestEventArgs { Request: ReceiveMessageRequest })
            {
                dispatched.TrySetResult();
            }
        };

        async Task<List<Message>> ReceiveAsync()
            => (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1 })).Messages ?? [];

        // ThreadPoolExecutor(max_workers=1).submit(_receive)
        var future = Task.Run(ReceiveAsync);
        var wasDispatched = await Task.WhenAny(dispatched.Task, FidelityTime.Delay(TimeSpan.FromSeconds(BudgetSeconds(ctx, 10)))) == dispatched.Task;
        Ensure(wasDispatched, "the ReceiveMessage request was never dispatched");
        // A short poll on an empty queue answers at once with nothing; only the inherited long
        // poll is still open to return the message sent after the request went out.
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "wakes-queue-level-poller" });
        var messages = await future.WaitAsync(TimeSpan.FromSeconds(BudgetSeconds(ctx, 40)));

        Ensure(messages.Count > 0, "the queue-level long poll returned no message");
        Ensure(messages[0].Body == "wakes-queue-level-poller", $"unexpected body: '{messages[0].Body}'");
        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = messages[0].ReceiptHandle });

        var drained = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 2 });
        Ensure(drained.Messages is null or { Count: 0 },
            $"queue still has a message after delete_message: [{string.Join(", ", (drained.Messages ?? []).Select(m => m.Body))}]");
    }

    /// <summary>A wait's timeout, bounded by the runner's remaining time so the wait fails with its own message.</summary>
    private static double BudgetSeconds(FidelityContext ctx, double capSeconds)
    {
        var left = ctx.TimeLeftSeconds;
        return Math.Max(1.0, Math.Min(capSeconds, left - 5.0));
    }
}

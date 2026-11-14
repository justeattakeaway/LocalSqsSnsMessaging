// Translated from behavioral/sqs/check_long_polling_wakes_on_message_arrival.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.Runtime;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.long-polling-wakes-on-message-arrival</summary>
[Category("AwsEvaluationSuite")]
public sealed class LongPollingWakesOnMessageArrival
{
    /// <summary>
    /// A long-poll ReceiveMessage returns the message sent while it waits before WaitTimeSeconds ends,
    /// and DeleteMessage then removes it from the queue.
    ///
    /// Reference: https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_ReceiveMessage.html
    /// </summary>
    [Test]
    [DisplayName("sqs.long-polling-wakes-on-message-arrival")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var queueName = FidelityContext.UniqueName("sim2real-sqs-longpoll-");
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = queueName, Tags = new() { ["sim2real-check"] = "long-polling-wakes-on-message-arrival" },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        const int waitTime = 18;
        var requestDispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Fire when the ReceiveMessage HTTP request is actually about to go over the
        // wire, not merely when the pool thread starts running — the latter races the
        // sender: a thread that has started but not yet dispatched its request lets
        // send_message land before AWS ever sees an open long-poll, which would make
        // this pass on the trivial "message already there" case instead of on a real
        // mid-wait wakeup.
        // (botocore's before-send.sqs.ReceiveMessage hook becomes the SDK's BeforeRequestEvent.)
        sqs.BeforeRequestEvent += (_, e) =>
        {
            if (e is WebServiceRequestEventArgs { Request: ReceiveMessageRequest })
            {
                requestDispatched.TrySetResult();
            }
        };

        async Task<(double Elapsed, List<Message> Messages)> LongPoll()
        {
            var start = Stopwatch.StartNew();
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = waitTime });
            return (start.Elapsed.TotalSeconds, resp.Messages ?? []);
        }

        var future = Task.Run(LongPoll);
        var signalled = await Task.WhenAny(requestDispatched.Task, Task.Delay(TimeSpan.FromSeconds(BudgetS(ctx, 10)))) == requestDispatched.Task;
        Ensure(signalled, "long-poll thread never dispatched its ReceiveMessage request");
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "wake-up-the-long-poller" });
        var (elapsed, messages) = await future.WaitAsync(TimeSpan.FromSeconds(BudgetS(ctx, waitTime + 30)));

        Ensure(messages.Count > 0, "long poll returned no message at all");
        Ensure(messages[0].Body == "wake-up-the-long-poller",
            $"unexpected body: '{messages[0].Body}'");
        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = messages[0].ReceiptHandle });

        // A poll that only returns once the full window elapses takes at least WaitTimeSeconds.
        Ensure(elapsed < waitTime,
            $"long poll took {elapsed:F1}s (>= WaitTimeSeconds {waitTime}), so it slept the full " +
            "window instead of waking up when the message arrived");

        // independent read: the delete actually removed it, not just the local handle
        var drained = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 0 });
        Ensure(drained.Messages is null or { Count: 0 },
            $"queue still has a message after delete_message: [{string.Join(", ", (drained.Messages ?? []).Select(m => m.Body))}]");
    }

    /// <summary>A wait's timeout, bounded by the runner's remaining time so the wait fails with its own message.</summary>
    private static double BudgetS(FidelityContext ctx, double capS)
    {
        var left = ctx.TimeLeftSeconds;
        return Math.Max(1.0, Math.Min(capS, left - 5.0));
    }
}

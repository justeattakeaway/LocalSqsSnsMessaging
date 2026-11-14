// Translated from behavioral/sqs/check_long_polling_returns_fast.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.Runtime;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.long-polling-returns-fast</summary>
[Category("AwsEvaluationSuite")]
public sealed class LongPollingReturnsFast
{
    /// <summary>
    /// A WaitTimeSeconds=5 long poll on an empty queue returns the message sent while it waits, before the wait ends.
    ///
    /// Reference: https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_ReceiveMessage.html
    /// </summary>
    [Test]
    [DisplayName("sqs.long-polling-returns-fast")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-"),
            Tags = new() { ["sim2real"] = "behavioral" },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var body = $"longpoll-{Guid.NewGuid().ToString("N")[..8]}";
        const int waitTime = 5;
        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Set as the ReceiveMessage request goes on the wire, so the send lands while the poll is open.
        // (botocore's before-send.sqs.ReceiveMessage hook becomes the SDK's BeforeRequestEvent.)
        sqs.BeforeRequestEvent += (_, e) =>
        {
            if (e is WebServiceRequestEventArgs { Request: ReceiveMessageRequest })
            {
                dispatched.TrySetResult();
            }
        };

        async Task<(double Elapsed, List<Message> Messages)> Receive()
        {
            var start = FidelityTime.StartNew();
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = waitTime });
            return (start.Elapsed.TotalSeconds, resp.Messages ?? []);
        }

        var future = Task.Run(Receive);
        var signalled = await Task.WhenAny(dispatched.Task, FidelityTime.Delay(TimeSpan.FromSeconds(BudgetS(ctx, 10)))) == dispatched.Task;
        Ensure(signalled, "the ReceiveMessage request was never dispatched");
        await ctx.Sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = body });
        var (elapsed, messages) = await future.WaitAsync(TimeSpan.FromSeconds(BudgetS(ctx, waitTime + 30)));

        // The queue is empty when the receive starts, so only a receive that is still waiting when the
        // send lands returns the message; returning it before WaitTimeSeconds is the documented wake.
        Ensure(messages.Count > 0, "long poll did not return the message sent while it waited");
        Ensure(messages[0].Body == body, $"body mismatch: '{messages[0].Body}' != '{body}'");
        Ensure(elapsed < waitTime,
            $"long poll took {elapsed:F1}s; a message that arrives mid-wait ends the poll before the " +
            $"{waitTime}s WaitTimeSeconds");
    }

    /// <summary>A wait's timeout, bounded by the runner's remaining time so the wait fails with its own message.</summary>
    private static double BudgetS(FidelityContext ctx, double capS)
    {
        var left = ctx.TimeLeftSeconds;
        return Math.Max(1.0, Math.Min(capS, left - 5.0));
    }
}

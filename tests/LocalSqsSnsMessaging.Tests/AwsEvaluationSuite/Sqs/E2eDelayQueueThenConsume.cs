// Translated from behavioral/sqs/check_e2e_delay_queue_then_consume.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.e2e-delay-queue-then-consume</summary>
[Category("AwsEvaluationSuite")]
public sealed class E2eDelayQueueThenConsume
{
    /// <summary>
    /// A message sent to a DelaySeconds queue is invisible to consumers until the
    /// delay elapses, and is then consumable exactly once end-to-end.
    /// </summary>
    [Test]
    [DisplayName("sqs.e2e-delay-queue-then-consume")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.QueueDelayAndWait, "queue-level DelaySeconds=8 is ignored: the message became visible before DelaySeconds elapsed")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        const int delaySeconds = 8;
        var queueUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-delayq-"),
            Attributes = new() { ["DelaySeconds"] = delaySeconds.ToString() },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(queueUrl));

        var body = $"delayed-{queueUrl}";
        var sendTime = Stopwatch.StartNew();
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = queueUrl, MessageBody = body });

        // Invariant: the message must not be visible before the delay elapses.
        while (sendTime.Elapsed.TotalSeconds < delaySeconds - 2.0)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 1, WaitTimeSeconds = 1 });
            Ensure(resp.Messages is null or { Count: 0 }, "message became visible before DelaySeconds elapsed");
        }

        // Now poll (bounded) until it does become visible and consume it exactly once.
        Message? received = null;
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed.TotalSeconds < 30.0 && received is null)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 1, WaitTimeSeconds = 5 });
            var msgs = resp.Messages ?? [];
            if (msgs.Count > 0)
            {
                received = msgs[0];
            }
        }

        Ensure(received is not null, "delayed message never became visible");
        Ensure(received.Body == body, "assert received[\"Body\"] == body");
        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = received.ReceiptHandle });

        // confirm it is gone for good (no re-delivery after delete)
        deadline = Stopwatch.StartNew();
        while (deadline.Elapsed.TotalSeconds < 10.0)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 1, WaitTimeSeconds = 2 });
            Ensure(resp.Messages is null or { Count: 0 }, "deleted message reappeared");
        }
    }
}

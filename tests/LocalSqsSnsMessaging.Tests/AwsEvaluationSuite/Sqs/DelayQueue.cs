// Translated from behavioral/sqs/check_delay_queue.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.delay-queue</summary>
[Category("AwsEvaluationSuite")]
public sealed class DelayQueue
{
    private static readonly Dictionary<string, string> Tags = new() { ["sim2real:managed"] = "true", ["sim2real"] = "delay-queue" };

    // timing:real-time — visibility: 2 s https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/sqs-delay-queues.html
    /// <summary>
    /// Queue with DelaySeconds=2: immediate receive is empty, after delay message arrives.
    /// </summary>
    [Test]
    [DisplayName("sqs.delay-queue")]
    [Timeout(FidelityContext.RealTimeBudgetMilliseconds)]
    [Category(ApplyDefaultCategoryAttribute.TimeBasedCategoryName)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.RealTimeBudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        const int delaySeconds = 2;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-"),
            Attributes = new() { ["DelaySeconds"] = delaySeconds.ToString() },
            Tags = new(Tags),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var body = $"delay-queue-{Guid.NewGuid().ToString("N")[..8]}";
        var sendTime = Stopwatch.StartNew();
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = body });
        // Immediate receive should be empty (message is delayed)
        var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 0 });
        var messages = resp.Messages ?? [];
        if (messages.Count > 0)
        {
            throw Fail($"expected no messages immediately after send to delay queue, got [{string.Join(", ", messages.Select(m => $"{m.MessageId}: {m.Body}"))}]");
        }

        // Bounded retry for the delay to expire — deadline is the declared DelaySeconds plus a margin
        // for SQS's own delivery latency, never an unconditional sleep.
        var deadline = TimeSpan.FromSeconds(delaySeconds + 15);
        var received = new List<Message>();
        var seen = new HashSet<string>();
        while (sendTime.Elapsed < deadline && received.Count == 0)
        {
            resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 1 });
            var msgs = resp.Messages ?? [];
            foreach (var m in msgs)
            {
                if (seen.Add(m.MessageId))
                {
                    received.Add(m);
                }

                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle });
            }
        }

        var elapsed = sendTime.Elapsed.TotalSeconds;
        if (received.Count == 0)
        {
            throw Fail($"message never arrived after {elapsed:F1}s waiting on a {delaySeconds}s delay queue");
        }

        if (elapsed < delaySeconds)
        {
            throw Fail(
                $"message became visible after only {elapsed:F1}s, before DelaySeconds={delaySeconds} could have elapsed");
        }

        if (received[0].Body != body)
        {
            throw Fail($"body mismatch: {received[0].Body} != {body}");
        }
    }
}

// Translated from behavioral/sqs/check_queue_level_wait_time.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.queue-level-wait-time</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueueLevelWaitTime
{
    /// <summary>
    /// Queue-level ReceiveMessageWaitTimeSeconds drives long polling with no per-call param.
    ///
    /// A receive that passes NO WaitTimeSeconds must inherit the queue attribute and
    /// block. Catches emulators that only honor the per-request WaitTimeSeconds.
    /// </summary>
    // timing:real-time — lease: 2 s https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/sqs-short-and-long-polling.html
    [Test]
    [DisplayName("sqs.queue-level-wait-time")]
    [Timeout(FidelityContext.RealTimeBudgetMilliseconds)]
    [Category(ApplyDefaultCategoryAttribute.TimeBasedCategoryName)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.RealTimeBudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-"),
            Attributes = new() { ["ReceiveMessageWaitTimeSeconds"] = "2" },
            Tags = new() { ["sim2real"] = "true" },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var start = Stopwatch.StartNew();
        var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1 });
        var elapsed = start.Elapsed.TotalSeconds;
        if (resp.Messages is { Count: > 0 })
        {
            throw Fail("expected empty receive on an empty queue");
        }

        if (elapsed < 1.5)
        {
            throw Fail(
                $"receive returned in {elapsed:F3}s; queue-level " +
                $"ReceiveMessageWaitTimeSeconds=2 was not honored");
        }

        // elapsed-bound: the queue's ReceiveMessageWaitTimeSeconds=2 bounds the long poll; the ceiling is the claim.
        if (elapsed > 6.0)
        {
            throw Fail(
                $"receive returned in {elapsed:F3}s; queue-level " +
                $"ReceiveMessageWaitTimeSeconds=2 should bound the wait, not hang well past it");
        }
    }
}

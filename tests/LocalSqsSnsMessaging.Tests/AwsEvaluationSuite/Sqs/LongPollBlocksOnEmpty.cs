// Translated from behavioral/sqs/check_long_poll_blocks_on_empty.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.long-poll-blocks-on-empty</summary>
[Category("AwsEvaluationSuite")]
public sealed class LongPollBlocksOnEmpty
{
    /// <summary>
    /// ReceiveMessage(WaitTimeSeconds=2) on an empty queue blocks for about 2s and returns no
    /// messages, proving the long-poll wait actually happens.
    /// </summary>
    [Test]
    [DisplayName("sqs.long-poll-blocks-on-empty")]
    [Timeout(FidelityContext.RealTimeBudgetMilliseconds)]
    [Category(ApplyDefaultCategoryAttribute.TimeBasedCategoryName)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.RealTimeBudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var start = Stopwatch.StartNew();
        var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 2 });
        var elapsed = start.Elapsed.TotalSeconds;
        if (resp.Messages is { Count: > 0 })
        {
            throw Fail("expected empty receive on an empty queue");
        }

        if (elapsed < 1.5)
        {
            throw Fail(
                $"long poll returned in {elapsed:F3}s; did not block for WaitTimeSeconds=2 " +
                "(emulator likely ignores long polling)");
        }
    }
}

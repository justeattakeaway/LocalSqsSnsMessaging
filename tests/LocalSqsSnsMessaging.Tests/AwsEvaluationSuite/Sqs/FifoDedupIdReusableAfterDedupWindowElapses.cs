// Translated from behavioral/sqs/check_fifo_dedup_id_reusable_after_dedup_window_elapses.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.fifo-dedup-id-reusable-after-dedup-window-elapses</summary>
[Category("AwsEvaluationSuite")]
public sealed class FifoDedupIdReusableAfterDedupWindowElapses
{
    // The deduplication interval is exactly the documented 5 minutes. A send inside the window with
    // the same MessageDeduplicationId is swallowed; a send issued after the window has fully elapsed
    // is NOT, and mints its own new MessageId. The resend is polled every 2 s (a send inside the window
    // is swallowed and never delivered) up to 330 s, a 30 s margin past the 300 s boundary.
    private const double DedupWindowWaitSeconds = 330.0;

    private static readonly Dictionary<string, string> Tags = new() { ["sim2real:managed"] = "true", ["sim2real"] = "fifo-dedup-window-elapses" };

    // timing:real-time — ttl: 300 s https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/FIFO-queues-exactly-once-processing.html
    /// <summary>
    /// A resend with the same MessageDeduplicationId is swallowed inside the 5-minute dedup window,
    /// but once the window has fully elapsed the same id mints a new MessageId and is delivered.
    /// </summary>
    [Test]
    [DisplayName("sqs.fifo-dedup-id-reusable-after-dedup-window-elapses")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [Category(ApplyDefaultCategoryAttribute.TimeBasedCategoryName)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.RealTimeBudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-") + ".fifo",
            Attributes = new() { ["FifoQueue"] = "true" },
            Tags = new(Tags),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var token = Guid.NewGuid().ToString("N")[..8];
        const string group = "sim2real-group";
        var dedupId = $"{token}-window";
        var bodyFirst = $"{token}-first";
        var bodySecond = $"{token}-second";
        var bodyThird = $"{token}-third";

        var first = await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = url,
            MessageBody = bodyFirst,
            MessageGroupId = group,
            MessageDeduplicationId = dedupId,
        });
        var second = await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = url,
            MessageBody = bodySecond,
            MessageGroupId = group,
            MessageDeduplicationId = dedupId,
        });
        Ensure(second.MessageId == first.MessageId,
            "positive control failed: a resend inside the dedup window minted a new MessageId " +
            $"{second.MessageId} instead of echoing {first.MessageId}");

        var clock = FidelityTime.StartNew();
        var deadline = Math.Min(DedupWindowWaitSeconds, ctx.TimeLeftSeconds - 30.0);
        SendMessageResponse third;
        while (true)
        {
            third = await sqs.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = url,
                MessageBody = bodyThird,
                MessageGroupId = group,
                MessageDeduplicationId = dedupId,
            });
            if (third.MessageId != first.MessageId || clock.Elapsed.TotalSeconds >= deadline)
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        Ensure(third.MessageId != first.MessageId,
            $"dedup window never elapsed: sends repeated for up to {DedupWindowWaitSeconds}s after the " +
            $"first still echoed its MessageId {first.MessageId} instead of minting a new one");

        var bodies = new List<string>();
        var messageIdsSeen = new HashSet<string>();
        var idle = 0;
        for (var i = 0; i < 15; i++)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 1 });
            var msgs = resp.Messages ?? [];
            if (msgs.Count == 0)
            {
                idle++;
                if (bodies.Count >= 2 && idle >= 3)
                {
                    break;
                }

                continue;
            }

            idle = 0;
            foreach (var m in msgs)
            {
                if (!messageIdsSeen.Add(m.MessageId))
                {
                    continue;
                }

                bodies.Add(m.Body);
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle });
            }
        }

        var expected = new[] { bodyFirst, bodyThird }.Order(StringComparer.Ordinal).ToList();
        var sortedBodies = bodies.Order(StringComparer.Ordinal).ToList();
        Ensure(sortedBodies.SequenceEqual(expected),
            "expected exactly the pre-window and post-window bodies " +
            $"[{string.Join(", ", expected)}] delivered, got [{string.Join(", ", sortedBodies)}] -- " +
            "the second (in-window) send must never have been enqueued at all");
    }
}

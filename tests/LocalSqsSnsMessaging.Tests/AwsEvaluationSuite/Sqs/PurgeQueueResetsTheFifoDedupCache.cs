// Translated from behavioral/sqs/check_purge_queue_resets_the_fifo_dedup_cache.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.purge-queue-resets-the-fifo-dedup-cache</summary>
[Category("AwsEvaluationSuite")]
public sealed class PurgeQueueResetsTheFifoDedupCache
{
    /// <summary>PurgeQueue "can take up to 60 seconds" (SQS API Reference, PurgeQueue).</summary>
    private const double PurgeWindowSeconds = 60.0;

    /// <summary>Receives poll up to this long for the post-purge message.</summary>
    private const double DrainTimeoutSeconds = 30.0;

    private static Dictionary<string, string> Tags => new()
    {
        ["sim2real:managed"] = "true",
        ["sim2real"] = "purge-queue-resets-the-fifo-dedup-cache",
    };

    /// <summary>
    /// Every (MessageId, Body) received, deleting each, up to and including ``last_id``; one FIFO
    /// group delivers in send order, so nothing sent before ``last_id`` can arrive after it.
    /// </summary>
    private static async Task<List<(string Id, string Body)>> DrainThrough(FidelityContext ctx, AmazonSQSClient sqs, string url, string lastId, double timeoutSeconds)
    {
        var seen = new List<(string Id, string Body)>();
        var left = ctx.TimeLeftSeconds;
        var budget = Math.Min(timeoutSeconds, left);
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(budget))
        {
            var msgs = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 })).Messages ?? [];
            foreach (var m in msgs)
            {
                seen.Add((m.MessageId, m.Body));
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle });
            }

            if (seen.Any(s => s.Id == lastId))
            {
                break;
            }
        }

        return seen;
    }

    /// <summary>
    /// A FIFO content-dedup resend before PurgeQueue reuses the first MessageId, but one right after
    /// PurgeQueue gets a new id; once the purge's 60 seconds pass, a resend of another pre-purge body
    /// gets a new id too and is delivered while neither pre-purge message ever is.
    /// </summary>
    // timing:real-time — cooldown: 60 s https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_PurgeQueue.html
    [Test]
    [DisplayName("sqs.purge-queue-resets-the-fifo-dedup-cache")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [Category(ApplyDefaultCategoryAttribute.TimeBasedCategoryName)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.RealTimeBudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-") + ".fifo",
            Attributes = new() { ["FifoQueue"] = "true", ["ContentBasedDeduplication"] = "true" },
            Tags = Tags,
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        const string group = "g";
        var body = FidelityContext.UniqueName("sim2real-behav-dedup-body-");
        var lateBody = FidelityContext.UniqueName("sim2real-behav-dedup-late-body-");

        async Task<string> Send(string b)
            => (await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = b, MessageGroupId = group })).MessageId
               ?? throw new KeyNotFoundException("MessageId");

        var firstId = await Send(body);
        var lateFirstId = await Send(lateBody);

        // Baseline, no purge yet: the dedup cache is live and collapses an identical resend.
        var baselineRepeatId = await Send(body);
        Ensure(baselineRepeatId == firstId,
            $"baseline: resending the same body with no purge in between got a NEW MessageId " +
            $"'{baselineRepeatId}', expected the deduplicated id '{firstId}' -- dedup is not " +
            $"behaving as documented in this environment, so the purge comparison below proves nothing");

        var purgeStart = FidelityTime.StartNew();
        await sqs.PurgeQueueAsync(new PurgeQueueRequest { QueueUrl = url });

        // The claim is specifically that this resolves BEFORE the (up to 60s) async drain completes.
        var postPurgeId = await Send(body);
        Ensure(postPurgeId != firstId,
            $"resending the same body immediately after PurgeQueue returned a MessageId equal to the " +
            $"pre-purge send ('{firstId}') -- PurgeQueue must reset the dedup cache, not just drain " +
            $"the messages");

        // A message sent while the purge runs may still be deleted by it, so delivery is read off a
        // resend made once the purge's 60 seconds have passed, inside the 5-minute dedup interval of
        // its pre-purge copy.
        var purgeEnd = TimeSpan.FromSeconds(PurgeWindowSeconds);
        while (purgeStart.Elapsed < purgeEnd)
        {
            var remaining = purgeEnd - purgeStart.Elapsed;
            await FidelityTime.Delay(remaining < TimeSpan.FromSeconds(5.0) ? (remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero) : TimeSpan.FromSeconds(5.0));
        }

        var lateId = await Send(lateBody);
        Ensure(lateId != lateFirstId,
            $"resending a pre-purge body once the purge completed returned its pre-purge MessageId " +
            $"'{lateFirstId}' -- PurgeQueue must reset the dedup cache");

        // Effect-absent + effect-present, read back: neither pre-purge id reappears, and the resend the
        // reset let through does, with the right body. Same group, so a pre-purge message could only
        // arrive before it.
        var delivered = await DrainThrough(ctx, sqs, url, lateId, timeoutSeconds: DrainTimeoutSeconds);
        var deliveredIds = delivered.Select(d => d.Id).ToHashSet();
        var shown = $"[{string.Join(", ", delivered)}]";
        Ensure(!deliveredIds.Contains(firstId) && !deliveredIds.Contains(lateFirstId),
            $"a pre-purge message ('{firstId}' or '{lateFirstId}') was delivered after PurgeQueue: {shown}");
        Ensure(deliveredIds.Contains(lateId), $"the post-purge, non-deduplicated message '{lateId}' was never delivered: {shown}");
        var lateBodies = delivered.Where(d => d.Id == lateId).Select(d => d.Body).ToList();
        Ensure(lateBodies.SequenceEqual([lateBody]),
            $"the post-purge message carried body [{string.Join(", ", lateBodies)}], expected ['{lateBody}']");
    }
}

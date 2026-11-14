// Translated from behavioral/sqs/check_queue_delay_change_is_retroactive_for_fifo_but_not_standard.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using System.Globalization;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.queue-delay-change-is-retroactive-for-fifo-but-not-standard</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueueDelayChangeIsRetroactiveForFifoButNotStandard
{
    private static Dictionary<string, string> Tags => new() { ["sim2real:managed"] = "true", ["sim2real"] = "queue-delay-retroactivity" };

    // The original delay is the 900s maximum, so a message sent under it cannot become receivable
    // inside this check's observation window unless the queue-level change reached it.
    private const int OriginalDelaySeconds = 900;

    // Bound on how long the lowered delay may take to propagate and release the messages it governs.
    private const double ConvergeSeconds = 150.0;

    private static async Task<List<Message>> Receive(AmazonSQSClient sqs, string url, int waitSeconds = 2)
        => (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = waitSeconds })).Messages ?? [];

    private static string Show(IEnumerable<string> values) => "[" + string.Join(", ", values.Select(v => $"'{v}'")) + "]";

    /// <summary>
    /// Lowering a queue's DelaySeconds releases messages already on a FIFO queue, but on a standard
    /// queue only messages sent after the change see it; the original message stays withheld.
    /// </summary>
    [Test]
    [DisplayName("sqs.queue-delay-change-is-retroactive-for-fifo-but-not-standard")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.Fifo, "lowering a FIFO queue's DelaySeconds does not release messages already waiting out the old delay")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var token = Guid.NewGuid().ToString("N")[..8];
        var original = OriginalDelaySeconds.ToString(CultureInfo.InvariantCulture);

        var stdUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-") + "-std",
            Attributes = new() { ["DelaySeconds"] = original },
            Tags = Tags,
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(stdUrl));

        var fifoUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-") + ".fifo",
            Attributes = new() { ["FifoQueue"] = "true", ["DelaySeconds"] = original },
            Tags = Tags,
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(fifoUrl));

        foreach (var url in new[] { stdUrl, fifoUrl })
        {
            var got = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["DelaySeconds"] })).Attributes!["DelaySeconds"];
            Ensure(got == original, $"queue did not take DelaySeconds={OriginalDelaySeconds}: '{got}'");
        }

        var stdOld = $"std-before-change-{token}";
        var fifoOld = $"fifo-before-change-{token}";
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = stdUrl, MessageBody = stdOld });
        await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = fifoUrl,
            MessageBody = fifoOld,
            MessageGroupId = "g",
            MessageDeduplicationId = $"old-{token}",
        });

        // Both are withheld under the original delay before anything changes.
        var before = (await Receive(sqs, stdUrl)).Concat(await Receive(sqs, fifoUrl)).Select(m => m.Body).ToList();
        Ensure(before.Count == 0, $"a message under a {OriginalDelaySeconds}s delay was delivered: {Show(before)}");

        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = stdUrl, Attributes = new() { ["DelaySeconds"] = "0" } });
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = fifoUrl, Attributes = new() { ["DelaySeconds"] = "0" } });

        // FIFO: the message already on the queue is released by the change.
        var fifoSeen = new List<string>();
        var deadline = Stopwatch.StartNew();
        while (!fifoSeen.Contains(fifoOld) && deadline.Elapsed < TimeSpan.FromSeconds(ConvergeSeconds))
        {
            foreach (var m in await Receive(sqs, fifoUrl, waitSeconds: 5))
            {
                fifoSeen.Add(m.Body);
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = fifoUrl, ReceiptHandle = m.ReceiptHandle });
            }
        }

        Ensure(fifoSeen.SequenceEqual([fifoOld]),
            $"lowering DelaySeconds on a FIFO queue must release the message already on it within " +
            $"{ConvergeSeconds:F0}s; received {Show(fifoSeen)}, expected {Show([fifoOld])}");

        // Standard: new messages see the change, the old one does not.
        var stdSeen = new List<string>();
        var probes = new List<string>();
        deadline = Stopwatch.StartNew();
        while (!probes.Any(stdSeen.Contains) && deadline.Elapsed < TimeSpan.FromSeconds(ConvergeSeconds))
        {
            var probe = $"std-after-change-{probes.Count}-{token}";
            probes.Add(probe);
            await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = stdUrl, MessageBody = probe });
            foreach (var m in await Receive(sqs, stdUrl, waitSeconds: 3))
            {
                stdSeen.Add(m.Body);
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = stdUrl, ReceiptHandle = m.ReceiptHandle });
            }
        }

        var arrived = probes.Where(stdSeen.Contains).ToList();
        Ensure(arrived.Count > 0,
            $"no message sent after DelaySeconds=0 on the standard queue arrived within " +
            $"{ConvergeSeconds:F0}s ({probes.Count} probes sent), so the change never took effect");
        // Give the original message the same window again after the change is proven live.
        var settle = Stopwatch.StartNew();
        while (settle.Elapsed < TimeSpan.FromSeconds(15))
        {
            foreach (var m in await Receive(sqs, stdUrl, waitSeconds: 5))
            {
                stdSeen.Add(m.Body);
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = stdUrl, ReceiptHandle = m.ReceiptHandle });
            }
        }

        Ensure(!stdSeen.Contains(stdOld),
            $"lowering DelaySeconds on a STANDARD queue released a message sent before the change; " +
            $"the per-queue delay is not retroactive there. Received {Show(stdSeen.Order(StringComparer.Ordinal))}");
        Ensure(stdSeen.ToHashSet().IsSubsetOf(probes),
            $"standard queue delivered bodies nobody sent after the change: " +
            $"{Show(stdSeen.Except(probes).Order(StringComparer.Ordinal))}");
    }
}

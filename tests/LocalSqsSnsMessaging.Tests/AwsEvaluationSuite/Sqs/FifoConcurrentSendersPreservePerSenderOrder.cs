// Translated from behavioral/sqs/check_fifo_concurrent_senders_preserve_per_sender_order.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.fifo-concurrent-senders-preserve-per-sender-order</summary>
[Category("AwsEvaluationSuite")]
public sealed class FifoConcurrentSendersPreservePerSenderOrder
{
    private const string Check = "fifo-concurrent-senders-preserve-per-sender-order";
    private static readonly Dictionary<string, string> Tags = new() { ["sim2real:managed"] = "true", ["sim2real"] = Check };

    /// <summary>
    /// Under concurrent senders to the same FIFO MessageGroupId, each sender's own send order is preserved.
    /// </summary>
    [Test]
    [DisplayName("sqs.fifo-concurrent-senders-preserve-per-sender-order")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-") + ".fifo",
            Attributes = new()
            {
                ["FifoQueue"] = "true",
                ["ContentBasedDeduplication"] = "true",
                // keep messages safely in-flight for the whole test so none are redelivered
                // (which would otherwise appear as duplicate entries and look like reordering).
                ["VisibilityTimeout"] = "180",
            },
            Tags = new(Tags),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        const int numThreads = 4;
        const int perThread = 5;

        async Task Sender(int threadId)
        {
            for (var i = 0; i < perThread; i++)
            {
                await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = $"t{threadId}-{i}", MessageGroupId = "shared-group" });
            }
        }

        await Task.WhenAll(Enumerable.Range(0, numThreads).Select(threadId => Task.Run(() => Sender(threadId))));

        var received = new List<Message>();
        var deadline = Stopwatch.StartNew();
        const int expectedTotal = numThreads * perThread;
        while (received.Count < expectedTotal && deadline.Elapsed.TotalSeconds < 60.0)
        {
            var batch = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 })).Messages ?? [];
            received.AddRange(batch);
            // FIFO queues only make further messages in a group visible once the previously
            // received (but undeleted) ones for that group are deleted or expire, so delete
            // as we go instead of waiting until the very end to collect everything.
            foreach (var m in batch)
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle });
            }
        }

        Ensure(received.Count == expectedTotal, $"expected {expectedTotal} messages, got {received.Count}");

        var bodiesInOrder = received.Select(m => m.Body).ToList();
        for (var threadId = 0; threadId < numThreads; threadId++)
        {
            var ownSequence = bodiesInOrder.Where(b => b.StartsWith($"t{threadId}-", StringComparison.Ordinal)).ToList();
            var expected = Enumerable.Range(0, perThread).Select(i => $"t{threadId}-{i}").ToList();
            Ensure(ownSequence.SequenceEqual(expected), $"thread {threadId} order violated: [{string.Join(", ", ownSequence)}]");
        }
    }
}

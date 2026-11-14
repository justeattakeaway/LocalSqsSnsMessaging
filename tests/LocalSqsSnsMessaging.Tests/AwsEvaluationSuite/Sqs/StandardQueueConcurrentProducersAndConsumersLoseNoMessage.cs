// Translated from behavioral/sqs/check_standard_queue_concurrent_producers_and_consumers_lose_no_message.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.standard-queue-concurrent-producers-and-consumers-lose-no-message</summary>
[Category("AwsEvaluationSuite")]
public sealed class StandardQueueConcurrentProducersAndConsumersLoseNoMessage
{
    private const int Producers = 3;
    private const int BatchesPerProducer = 5;
    private const int Consumers = 6;
    private const double DrainDeadlineSeconds = 180.0;
    private const double SettleSeconds = 90.0;

    private static Dictionary<string, string> Tags() => new() { ["sim2real"] = "standard-queue-concurrent-producers-and-consumers-lose-no-message" };

    /// <summary>
    /// Concurrent batch producers and consumers on one standard queue lose no message and receive no
    /// phantom: every body arrives with a matching MD5OfBody, no batch entry fails, and the counters
    /// reach 0. Duplicates are allowed on a standard queue and are not asserted either way.
    /// </summary>
    [Test]
    [DisplayName("sqs.standard-queue-concurrent-producers-and-consumers-lose-no-message")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-conc-pc-"),
            Attributes = new() { ["VisibilityTimeout"] = "60" },
            Tags = Tags(),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var token = FidelityContext.UniqueName("b");
        var gate = new Lock();
        var sent = new HashSet<string>(StringComparer.Ordinal);
        var received = new HashSet<string>(StringComparer.Ordinal);
        var problems = new List<string>();
        using var producersDone = new ManualResetEventSlim(false);
        var deadline = Stopwatch.StartNew();

        async Task Producer(int p)
        {
            for (var b = 0; b < BatchesPerProducer; b++)
            {
                var entries = Enumerable.Range(0, 10).Select(k => new SendMessageBatchRequestEntry { Id = $"e{k}", MessageBody = $"{token}|{p}|{b}|{k}" }).ToList();
                var resp = await sqs.SendMessageBatchAsync(new SendMessageBatchRequest { QueueUrl = url, Entries = entries });
                lock (gate)
                {
                    if (resp.Failed is { Count: > 0 })
                    {
                        problems.Add($"send failed: {string.Join(", ", resp.Failed.Select(f => $"{f.Id}:{f.Code}"))}");
                    }

                    var ok = (resp.Successful ?? []).Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
                    sent.UnionWith(entries.Where(e => ok.Contains(e.Id)).Select(e => e.MessageBody));
                }
            }
        }

        async Task Consumer(int _)
        {
            while (deadline.Elapsed < TimeSpan.FromSeconds(DrainDeadlineSeconds))
            {
                lock (gate)
                {
                    if (producersDone.IsSet && sent.Count == sent.Intersect(received).Count())
                    {
                        return;
                    }
                }

                var msgs = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 })).Messages ?? [];
                if (msgs.Count == 0)
                {
                    continue;
                }

                lock (gate)
                {
                    foreach (var m in msgs)
                    {
                        if (m.MD5OfBody != Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(m.Body))))
                        {
                            problems.Add($"MD5OfBody mismatch for '{m.Body}'");
                        }

                        received.Add(m.Body);
                    }
                }

                var resp = await sqs.DeleteMessageBatchAsync(new DeleteMessageBatchRequest
                {
                    QueueUrl = url,
                    Entries = [.. msgs.Select((m, i) => new DeleteMessageBatchRequestEntry { Id = $"d{i}", ReceiptHandle = m.ReceiptHandle })],
                });
                if (resp.Failed is { Count: > 0 })
                {
                    lock (gate)
                    {
                        problems.Add($"delete failed: {string.Join(", ", resp.Failed.Select(f => $"{f.Id}:{f.Code}"))}");
                    }
                }
            }
        }

        var consumers = Enumerable.Range(0, Consumers).Select(c => Task.Run(() => Consumer(c))).ToList();
        var producers = Enumerable.Range(0, Producers).Select(p => Task.Run(() => Producer(p))).ToList();
        foreach (var f in producers)
        {
            await f;
        }

        producersDone.Set();
        foreach (var f in consumers)
        {
            await f;
        }

        const int expected = Producers * BatchesPerProducer * 10;
        Ensure(problems.Count == 0, $"concurrent producers/consumers hit: [{string.Join(", ", problems.Take(5))}]");
        Ensure(sent.Count == expected, $"{sent.Count} of {expected} sends were acknowledged");
        var missing = sent.Except(received).ToList();
        Ensure(missing.Count == 0,
            $"{missing.Count} acknowledged message(s) were never received by any of {Consumers} " +
            $"concurrent consumers within {DrainDeadlineSeconds}s, e.g. [{string.Join(", ", missing.Order(StringComparer.Ordinal).Take(5))}]");
        var phantom = received.Except(sent).ToList();
        Ensure(phantom.Count == 0, $"consumers received bodies nobody sent: [{string.Join(", ", phantom.Order(StringComparer.Ordinal).Take(5))}]");

        Dictionary<string, string> counts = [];
        var settle = Stopwatch.StartNew();
        while (settle.Elapsed < TimeSpan.FromSeconds(SettleSeconds))
        {
            counts = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = url,
                AttributeNames = ["ApproximateNumberOfMessages", "ApproximateNumberOfMessagesNotVisible"],
            })).Attributes ?? throw new KeyNotFoundException("'Attributes'");
            if (BothZero(counts))
            {
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(3));
        }

        Ensure(BothZero(counts),
            $"after every message was received and deleted the counters read {{{string.Join(", ", counts.Select(kv => $"'{kv.Key}': '{kv.Value}'"))}}}, " +
            $"expected both 0 within {SettleSeconds}s");
    }

    private static bool BothZero(Dictionary<string, string> counts)
        => counts.Count == 2
           && counts.GetValueOrDefault("ApproximateNumberOfMessages") == "0"
           && counts.GetValueOrDefault("ApproximateNumberOfMessagesNotVisible") == "0";
}

// Translated from behavioral/sqs/check_fifo_message_group_held_by_one_concurrent_receiver_at_a_time.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.fifo-message-group-held-by-one-concurrent-receiver-at-a-time</summary>
[Category("AwsEvaluationSuite")]
public sealed class FifoMessageGroupHeldByOneConcurrentReceiverAtATime
{
    private const int Groups = 3;
    private const int PerGroup = 8;
    private const int Receivers = 12;
    private const int VisibilityS = 300;
    private const int MaxRounds = 30;

    private static readonly Dictionary<string, string> Tags = new()
    {
        ["sim2real"] = "fifo-message-group-held-by-one-concurrent-receiver-at-a-time",
    };

    /// <summary>
    /// Concurrent receivers never split a FIFO group: its in-flight messages sit in ONE batch.
    ///
    /// A held group stays locked across rounds, and draining delivers each message once, in order.
    /// </summary>
    [Test]
    [DisplayName("sqs.fifo-message-group-held-by-one-concurrent-receiver-at-a-time")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var token = FidelityContext.UniqueName("g");
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-fifo-grp-") + ".fifo",
            Attributes = new()
            {
                ["FifoQueue"] = "true", ["ContentBasedDeduplication"] = "true",
                ["VisibilityTimeout"] = VisibilityS.ToString(CultureInfo.InvariantCulture),
            },
            Tags = new(Tags),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var groups = Enumerable.Range(0, Groups).Select(k => $"{token}-{k}").ToList();
        for (var i = 0; i < PerGroup; i++)
        {
            foreach (var g in groups)
            {
                await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = $"{g}|{i}", MessageGroupId = g });
            }
        }

        // Two rounds with nothing deleted: the second round shows a held group stays locked.
        var heldBatches = (await ConcurrentRound(sqs, url)).Concat(await ConcurrentRound(sqs, url)).ToList();
        var owners = new Dictionary<string, int>();
        var byBatch = GroupsByBatch(heldBatches);
        for (var idx = 0; idx < byBatch.Count; idx++)
        {
            foreach (var (g, order) in byBatch[idx])
            {
                Ensure(groups.Contains(g), $"a message from an unknown group '{g}' was received");
                Ensure(!owners.ContainsKey(g),
                    $"group '{g}' was handed to two receive batches while its messages were in flight " +
                    $"(batches {(owners.TryGetValue(g, out var o) ? o : -1)} and {idx}); FIFO must lock the group to one receiver");
                owners[g] = idx;
                Ensure(order.SequenceEqual(Enumerable.Range(0, order.Count)),
                    $"the first batch holding group '{g}' must hold its head messages in send order, " +
                    $"got indexes [{string.Join(", ", order)}]");
            }
        }

        Ensure(owners.Count > 0, "two rounds of twelve concurrent receives returned no message at all");

        var delivered = groups.ToDictionary(g => g, _ => new List<int>());
        var pending = heldBatches.SelectMany(b => b).ToList();
        foreach (var held in GroupsByBatch(heldBatches))
        {
            foreach (var (g, order) in held)
            {
                delivered[g].AddRange(order);
            }
        }

        const int total = Groups * PerGroup;
        for (var round = 0; round < MaxRounds; round++)
        {
            foreach (var m in pending)
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle });
            }

            if (delivered.Values.Sum(v => v.Count) >= total)
            {
                break;
            }

            var batches = await ConcurrentRound(sqs, url);
            var seenInRound = new Dictionary<string, int>();
            var roundByBatch = GroupsByBatch(batches);
            for (var idx = 0; idx < roundByBatch.Count; idx++)
            {
                foreach (var (g, order) in roundByBatch[idx])
                {
                    Ensure(!seenInRound.ContainsKey(g),
                        $"group '{g}' appeared in two batches of one concurrent round " +
                        $"({(seenInRound.TryGetValue(g, out var s) ? s : -1)} and {idx})");
                    seenInRound[g] = idx;
                    var nxt = delivered[g].Count;
                    Ensure(order.SequenceEqual(Enumerable.Range(nxt, order.Count)),
                        $"group '{g}' continued with indexes [{string.Join(", ", order)}]; after {nxt} delivered " +
                        $"messages the next batch must start at {nxt} and stay in order");
                    delivered[g].AddRange(order);
                }
            }

            pending = batches.SelectMany(b => b).ToList();
        }

        foreach (var g in groups)
        {
            Ensure(delivered[g].SequenceEqual(Enumerable.Range(0, PerGroup)),
                $"group '{g}' was delivered as [{string.Join(", ", delivered[g])}]; expected each of its {PerGroup} " +
                $"messages exactly once, in order, within {MaxRounds} rounds");
        }
    }

    /// <summary>Fire one ReceiveMessage per receiver, all released together, and return every batch.</summary>
    private static async Task<List<List<Message>>> ConcurrentRound(AmazonSQSClient sqs, string url)
    {
        // threading.Barrier(_RECEIVERS) with barrier.wait(timeout=30): every receiver waits until all have arrived.
        var arrived = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<List<Message>> Receive()
        {
            if (Interlocked.Increment(ref arrived) == Receivers)
            {
                release.SetResult();
            }

            await release.Task.WaitAsync(TimeSpan.FromSeconds(30));
            return (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 1,
            })).Messages ?? [];
        }

        var tasks = Enumerable.Range(0, Receivers).Select(_ => Task.Run(Receive)).ToList();
        return [.. await Task.WhenAll(tasks)];
    }

    private static List<List<(string Group, List<int> Order)>> GroupsByBatch(List<List<Message>> batches)
    {
        var output = new List<List<(string, List<int>)>>();
        foreach (var batch in batches)
        {
            // dict insertion order: first appearance of each group in the batch
            var held = new List<(string Group, List<int> Order)>();
            foreach (var m in batch)
            {
                var sep = m.Body.IndexOf('|', StringComparison.Ordinal);
                var (g, i) = sep < 0 ? (m.Body, "") : (m.Body[..sep], m.Body[(sep + 1)..]);
                var entry = held.FindIndex(h => h.Group == g);
                if (entry < 0)
                {
                    held.Add((g, []));
                    entry = held.Count - 1;
                }

                held[entry].Order.Add(int.Parse(i, CultureInfo.InvariantCulture));
            }

            output.Add(held);
        }

        return output;
    }
}

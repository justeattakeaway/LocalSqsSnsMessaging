// Translated from behavioral/sqs/check_conc_limits_and_fanout_in_flight_disjoint_and_cmv_zero_redelivers_exact_subset.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using System.Globalization;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.conc-limits-and-fanout-in-flight-disjoint-and-cmv-zero-redelivers-exact-subset</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConcLimitsAndFanoutInFlightDisjointAndCmvZeroRedeliversExactSubset
{
    private const string Check = "conc-limits-and-fanout-in-flight-disjoint-and-cmv-zero-redelivers-exact-subset";
    private const int N = 30;          // messages, one MessageGroupId each
    private const int Receivers = 6;   // concurrent receivers
    private const int Vt = 600;        // queue VisibilityTimeout, seconds -- the window every "hidden" claim lives in
    private const int Reset = 7;       // messages whose visibility is reset to 0
    private const int BatchSize = 2;   // MaxNumberOfMessages per receive during a drain

    /// <summary>
    /// Six concurrent receivers on a FIFO queue get disjoint messages covering all 30; in-flight
    /// ones stay hidden, and visibility 0 on seven redelivers exactly those seven (receive count 2).
    /// </summary>
    [Test]
    [DisplayName("sqs.conc-limits-and-fanout-in-flight-disjoint-and-cmv-zero-redelivers-exact-subset")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        // FIFO because standard queues are at-least-once (disjointness is not guaranteed); one group
        // per message avoids FIFO head-of-line blocking.
        var unique = FidelityContext.UniqueName("sim2real-conc-inflight-");
        var name = unique[..Math.Min(75, unique.Length)] + ".fifo";
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = name,
            Attributes = new() { ["FifoQueue"] = "true", ["VisibilityTimeout"] = Vt.ToString(CultureInfo.InvariantCulture) },
            Tags = new() { ["sim2real"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var sent = new Dictionary<string, string>();
        for (var b = 0; b < N; b += 10)
        {
            var entries = Enumerable.Range(b, Math.Min(b + 10, N) - b).Select(i => new SendMessageBatchRequestEntry
            {
                Id = $"e{i}", MessageBody = $"m{i}", MessageGroupId = $"g{i}", MessageDeduplicationId = $"d{i}",
            }).ToList();
            var resp = await sqs.SendMessageBatchAsync(new SendMessageBatchRequest { QueueUrl = url, Entries = entries });
            Ensure(resp.Failed is null or { Count: 0 }, $"send_message_batch reported failures: [{string.Join(", ", (resp.Failed ?? []).Select(f => f.Id))}]");
            foreach (var ok in resp.Successful!)
            {
                sent[ok.MessageId] = ok.Id;
            }
        }

        Ensure(sent.Count == N, $"{sent.Count} distinct MessageIds acknowledged for {N} sends");

        var gate = new Lock();
        var firstReceive = new List<Stopwatch>();

        // Concurrent receivers until `target` distinct messages have been collected or the
        // budget ends. Returns every (receiver, MessageId, ReceiptHandle, ReceiveCount) seen.
        async Task<List<(int Receiver, string MessageId, string ReceiptHandle, int ReceiveCount)>> Drain(int target, double budgetS)
        {
            var seen = new List<(int Receiver, string MessageId, string ReceiptHandle, int ReceiveCount)>();
            var stop = Stopwatch.StartNew();
            // Every receiver starts together, and each call takes at most _BATCH, so the drain needs
            // many overlapping calls rather than one fast receiver taking the whole queue.
            var start = new AsyncBarrier(Receivers);

            async Task<List<(int, string, string, int)>> Receiver(int r)
            {
                var mine = new List<(int, string, string, int)>();
                await start.Wait(TimeSpan.FromSeconds(30));
                while (stop.Elapsed < TimeSpan.FromSeconds(budgetS))
                {
                    lock (gate)
                    {
                        if (seen.Select(s => s.MessageId).Distinct().Count() >= target)
                        {
                            break;
                        }
                    }

                    var got = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
                    {
                        QueueUrl = url, MaxNumberOfMessages = BatchSize, WaitTimeSeconds = 1, AttributeNames = ["ApproximateReceiveCount"],
                    })).Messages ?? [];
                    lock (gate)
                    {
                        if (got.Count > 0 && firstReceive.Count == 0)
                        {
                            firstReceive.Add(Stopwatch.StartNew());
                        }

                        foreach (var m in got)
                        {
                            var rec = (r, m.MessageId, m.ReceiptHandle, int.Parse(m.Attributes!["ApproximateReceiveCount"], CultureInfo.InvariantCulture));
                            seen.Add(rec);
                            mine.Add(rec);
                        }
                    }
                }

                return mine;
            }

            await Task.WhenAll(Enumerable.Range(0, Receivers).Select(r => Task.Run(() => Receiver(r))));
            return seen;
        }

        void StillInsideWindow(string label)
        {
            var elapsed = firstReceive[0].Elapsed.TotalSeconds;
            Ensure(elapsed < Vt - 60,
                $"{label}: {elapsed:F0}s have passed since the first receive, too close to the " +
                $"{Vt}s visibility window for a \"still hidden\" claim to be meaningful");
        }

        async Task<List<string>> EmptyRound()
        {
            var batches = await Task.WhenAll(Enumerable.Range(0, Receivers).Select(_ => Task.Run(async () =>
                (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 })).Messages ?? [])));
            return [.. batches.SelectMany(b => b).Select(m => m.MessageId)];
        }

        // Round 1: concurrent drain of the whole queue.
        var round1 = await Drain(N, budgetS: 90);
        var ids1 = round1.Select(s => s.MessageId).ToList();
        var dupes = ids1.GroupBy(i => i).Where(g => g.Count() > 1).Select(g => g.Key).Order(StringComparer.Ordinal).ToList();
        Ensure(dupes.Count == 0,
            $"{dupes.Count} messages were handed to more than one receive inside the visibility " +
            $"window: [{string.Join(", ", dupes.Select(d => sent.GetValueOrDefault(d)))}]");
        Ensure(ids1.ToHashSet().SetEquals(sent.Keys),
            $"the concurrent drain collected {ids1.Distinct().Count()} of {N}; missing " +
            $"[{string.Join(", ", sent.Keys.Except(ids1).Select(i => sent[i]).Order(StringComparer.Ordinal))}], unknown " +
            $"[{string.Join(", ", ids1.Except(sent.Keys).Order(StringComparer.Ordinal))}]");
        var perReceiver = round1.Select(s => s.Receiver).ToHashSet();
        Ensure(perReceiver.Count >= 2,
            $"only receiver(s) [{string.Join(", ", perReceiver.Order())}] got anything -- the drain was not concurrent, " +
            "so disjointness was never exercised");
        Ensure(round1.Select(s => s.ReceiveCount).ToHashSet().SetEquals([1]),
            $"first-round ApproximateReceiveCount values [{string.Join(", ", round1.Select(s => s.ReceiveCount).Distinct().Order())}], expected 1");

        // Everything is in flight: a further concurrent round returns nothing.
        StillInsideWindow("after round 1");
        var leaked = await EmptyRound();
        Ensure(leaked.Count == 0,
            $"with all {N} messages in flight, concurrent receives still returned " +
            $"[{string.Join(", ", leaked.Select(i => sent.GetValueOrDefault(i)))}]");

        // Reset visibility on exactly seven, spread across whichever receivers got them.
        var handles = new Dictionary<string, string>();
        foreach (var s in round1)
        {
            handles[s.MessageId] = s.ReceiptHandle;
        }

        var subset = handles.Keys.Order(StringComparer.Ordinal).Take(Reset).ToList();
        foreach (var mid in subset)
        {
            await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest { QueueUrl = url, ReceiptHandle = handles[mid], VisibilityTimeout = 0 });
        }

        var round2 = await Drain(Reset, budgetS: 60);
        var ids2 = round2.Select(s => s.MessageId).ToList();
        var dupes2 = ids2.GroupBy(i => i).Where(g => g.Count() > 1).Select(g => g.Key).Order(StringComparer.Ordinal).ToList();
        Ensure(dupes2.Count == 0, $"reset messages delivered twice in round 2: [{string.Join(", ", dupes2.Select(d => sent.GetValueOrDefault(d)))}]");
        Ensure(ids2.ToHashSet().SetEquals(subset),
            $"after resetting visibility on [{string.Join(", ", subset.Select(i => sent[i]).Order(StringComparer.Ordinal))}], round 2 returned " +
            $"[{string.Join(", ", ids2.Select(i => sent.GetValueOrDefault(i, i)).Order(StringComparer.Ordinal))}]; expected exactly the reset subset");
        var counts = new Dictionary<string, int>();
        foreach (var s in round2)
        {
            counts[sent[s.MessageId]] = s.ReceiveCount;
        }

        Ensure(counts.Values.ToHashSet().SetEquals([2]),
            $"redelivered messages report ApproximateReceiveCount {{{string.Join(", ", counts.Select(kv => $"{kv.Key}: {kv.Value}"))}}}; each was received " +
            "exactly twice");

        // The complement is still hidden, and so is the subset now that round 2 holds it.
        StillInsideWindow("after round 2");
        var leaked2 = await EmptyRound();
        Ensure(leaked2.Count == 0,
            "after round 2 every message is in flight again, yet concurrent receives returned " +
            $"[{string.Join(", ", leaked2.Select(i => sent.GetValueOrDefault(i)))}]");
    }

    /// <summary><c>threading.Barrier(parties)</c>; <c>Wait</c> times out like <c>Barrier.wait(timeout=...)</c>.</summary>
    private sealed class AsyncBarrier(int parties)
    {
        private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public async Task Wait(TimeSpan timeout)
        {
            if (Interlocked.Increment(ref _arrived) == parties)
            {
                _all.TrySetResult();
            }

            await _all.Task.WaitAsync(timeout);
        }
    }
}

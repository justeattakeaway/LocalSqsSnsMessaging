// Translated from behavioral/sqs/check_fifo_twenty_group_head_of_line_isolation_at_volume.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.fifo-twenty-group-head-of-line-isolation-at-volume</summary>
[Category("AwsEvaluationSuite")]
public sealed class FifoTwentyGroupHeadOfLineIsolationAtVolume
{
    private static readonly string[] Groups = [.. Enumerable.Range(0, 20).Select(i => $"g{i:D2}")];
    private const int PerGroup = 25;
    private static readonly int Total = Groups.Length * PerGroup;          // 500
    private const double DrainDeadlineS = 240.0;
    private const double DrainIdleS = 8.0;          // a receiver gives up after this long seeing only empty receives

    private static readonly Dictionary<string, string> Tags = new()
    {
        ["sim2real:managed"] = "true",
        ["sim2real"] = "fifo-twenty-group-head-of-line-isolation-at-volume",
    };

    /// <summary>
    /// Holding one FIFO group's head blocks exactly that group and nothing else, at volume.
    ///
    /// With 20 groups, 0/1/2 held heads drain 500/475/450 messages, each group in order.
    /// </summary>
    [Test]
    [DisplayName("sqs.fifo-twenty-group-head-of-line-isolation-at-volume")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        // random.Random(0).sample(...) becomes a System.Random(0) shuffle: a different permutation
        // than CPython's, but the check only needs some non-identity permutation.
        var rng = new Random(0);
        var expectAll = Groups.ToDictionary(g => g, _ => Enumerable.Range(0, PerGroup).ToArray());

        // Nothing held: the baseline that makes the stall arms' shortfall attributable.
        var controlUrl = await MakeQueue(sqs, ctx);
        await SendInterleaved(sqs, controlUrl, "control");
        var controlObs = await Drain(ctx, sqs, controlUrl, Total);
        var (cCount, cDistinct, cOrdered) = Verdict(controlObs);
        Ensure(cCount == Total,
            $"control arm drained {cCount} of {Total} with nothing held — the 475 below would not " +
            "be attributable to the stall");
        Ensure(cDistinct == Total, $"control arm delivered duplicates: {cCount} received, {cDistinct} distinct");
        Ensure(OrderedEquals(cOrdered, expectAll),
            "control arm broke per-group order: " +
            $"{Diff(cOrdered, expectAll)}");

        // One head held: 19 groups must drain completely; the 20th must not move.
        var oneUrl = await MakeQueue(sqs, ctx);
        var seqs = await SendInterleaved(sqs, oneUrl, "one");
        foreach (var (group, groupSeqs) in seqs.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            Ensure(groupSeqs.Count == PerGroup,
                $"group '{group}' was acknowledged {groupSeqs.Count} times, expected {PerGroup}");
            var asInts = groupSeqs.Select(s => BigInteger.Parse(s, CultureInfo.InvariantCulture)).ToList();
            Ensure(asInts.Zip(asInts.Skip(1)).All(p => p.First < p.Second),
                $"group '{group}' SequenceNumbers are not strictly increasing in send order: [{string.Join(", ", groupSeqs)}]");
        }

        var held = await HoldGroupHead(ctx, sqs, oneUrl, "g00");
        await AwaitCounters(ctx, sqs, oneUrl, Total - 1, 1);

        var oneObs = await Drain(ctx, sqs, oneUrl, Total - PerGroup);
        var (count, distinct, ordered) = Verdict(oneObs);
        var expect19 = Groups.Where(g => g != "g00").ToDictionary(g => g, _ => Enumerable.Range(0, PerGroup).ToArray());
        Ensure(count == Total - PerGroup,
            $"with g00's head in flight, 19 groups drained {count} messages, expected " +
            $"{Total - PerGroup}; groups seen were [{string.Join(", ", oneObs.Select(o => o.Group).Distinct().Order(StringComparer.Ordinal))}]");
        Ensure(!oneObs.Any(o => o.Group == "g00"),
            "a message from the stalled group g00 was delivered while its head was in flight: " +
            $"[{string.Join(", ", oneObs.Where(o => o.Group == "g00").Select(o => o.Ordinal).Order())}]");
        Ensure(distinct == Total - PerGroup,
            $"8 concurrent receivers delivered duplicates: {count} received, {distinct} distinct");
        Ensure(OrderedEquals(ordered, expect19),
            "per-group order or completeness broke under 8 concurrent receivers: " +
            $"{Diff(ordered, expect19)}");

        // Shuffle invariance: the verdict must not depend on the cross-group interleaving.
        var shuffled = Sample(rng, oneObs);
        Ensure(!shuffled.SequenceEqual(oneObs), "shuffle produced the identical list; the invariance test would be vacuous");
        Ensure(VerdictEquals(Verdict(shuffled), (count, distinct, ordered)),
            "the verdict function is NOT shuffle-invariant, so this check depends on the order in " +
            "which groups interleaved — which SQS does not define");

        // Counters model per-group state too: the 24 blocked siblings are VISIBLE, not in flight.
        await AwaitCounters(ctx, sqs, oneUrl, PerGroup - 1, 1);

        // Release the head: the stalled group's remaining 24 arrive, in order, behind it.
        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = oneUrl, ReceiptHandle = held.ReceiptHandle });
        var released = await Drain(ctx, sqs, oneUrl, PerGroup - 1, workers: 1);
        var (rCount, _, rOrdered) = Verdict(released);
        Ensure(rCount == PerGroup - 1,
            $"after deleting the held head, {rCount} messages drained, expected {PerGroup - 1}");
        Ensure(OrderedEquals(rOrdered, new Dictionary<string, int[]> { ["g00"] = [.. Enumerable.Range(1, PerGroup - 1)] }),
            $"the released group drained as {Repr(rOrdered)}, expected g00 ordinals 1..{PerGroup - 1}");

        // Two heads held: the isolation is per group, not one special group.
        var twoUrl = await MakeQueue(sqs, ctx);
        await SendInterleaved(sqs, twoUrl, "two");
        var heldA = await HoldGroupHead(ctx, sqs, twoUrl, "g00");
        var heldB = await HoldGroupHead(ctx, sqs, twoUrl, "g07");
        Ensure(heldA.ReceiptHandle != heldB.ReceiptHandle,
            $"holding g00 and g07 yielded the SAME receipt handle '{heldA.ReceiptHandle}' — " +
            "the two stalled heads were not actually distinct in-flight messages");
        var twoObs = await Drain(ctx, sqs, twoUrl, Total - 2 * PerGroup);
        var (tCount, tDistinct, tOrdered) = Verdict(twoObs);
        var expect18 = Groups.Where(g => g is not ("g00" or "g07")).ToDictionary(g => g, _ => Enumerable.Range(0, PerGroup).ToArray());
        Ensure(tCount == Total - 2 * PerGroup,
            $"with two heads in flight, {tCount} messages drained, expected {Total - 2 * PerGroup}");
        Ensure(tDistinct == tCount, $"two-stall arm delivered duplicates: {tCount} vs {tDistinct} distinct");
        Ensure(OrderedEquals(tOrdered, expect18),
            "two-stall arm: 18 groups did not drain completely and in order: " +
            $"{Diff(tOrdered, expect18)}");
        Ensure(VerdictEquals(Verdict(Sample(rng, twoObs)), (tCount, tDistinct, tOrdered)),
            "the two-stall verdict is not shuffle-invariant");
    }

    /// <summary>
    /// A poll's timeout: cap_s, bounded by the runner's remaining time less 5 s so the poll fails
    /// with its own message.
    /// </summary>
    private static double BudgetS(FidelityContext ctx, double capS)
    {
        var left = ctx.TimeLeftSeconds;
        return Math.Max(1.0, Math.Min(capS, left - 5.0));
    }

    /// <summary>True when the runner's remaining time, not the wait's own deadline, ended a capped wait.</summary>
    private static bool BudgetSpent(FidelityContext ctx) => ctx.TimeLeftSeconds <= 6.0;

    /// <summary>A FIFO queue whose in-flight window is long enough that a held head stays held.</summary>
    private static async Task<string> MakeQueue(AmazonSQSClient sqs, FidelityContext ctx)
    {
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-d5-") + ".fifo",
            Attributes = new()
            {
                ["FifoQueue"] = "true", ["ContentBasedDeduplication"] = "true",
                ["VisibilityTimeout"] = "600", ["ReceiveMessageWaitTimeSeconds"] = "0",
            },
            Tags = new(Tags),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        return url;
    }

    private static string Body(string group, int ordinal, string arm) => $"{arm}|{group}|{ordinal:D2}";

    private static (string Group, int Ordinal) Parse(string body)
    {
        var parts = body.Split('|');
        if (parts.Length != 3)
        {
            throw new FormatException($"not enough values to unpack (expected 3, got {parts.Length})");
        }

        return (parts[1], int.Parse(parts[2], CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Send 500 messages, index i -> group i % 20, ordinal i // 20.
    ///
    /// Consecutive sends never share a group, so global and per-group order disagree.
    /// </summary>
    private static async Task<Dictionary<string, List<string>>> SendInterleaved(AmazonSQSClient sqs, string url, string arm)
    {
        var seqs = new Dictionary<string, List<string>>();
        for (var b = 0; b < Total; b += 10)
        {
            var baseIndex = b;
            List<SendMessageBatchRequestEntry> entries = [];
            for (var i = baseIndex; i < baseIndex + 10; i++)
            {
                var group = Groups[i % Groups.Length];
                entries.Add(new SendMessageBatchRequestEntry
                {
                    Id = $"e{i - baseIndex}",
                    MessageBody = Body(group, i / Groups.Length, arm),
                    MessageGroupId = group,
                });
            }

            var resp = await sqs.SendMessageBatchAsync(new SendMessageBatchRequest { QueueUrl = url, Entries = entries });
            var failed = resp.Failed ?? [];
            Ensure(failed.Count == 0, $"send_message_batch at base {baseIndex} reported failures [{string.Join(", ", failed.Select(f => $"{f.Id}:{f.Code}"))}]");
            Ensure(resp.Successful!.Count == 10,
                $"send_message_batch at base {baseIndex} acknowledged {resp.Successful.Count} of 10");
            foreach (var entry in resp.Successful)
            {
                var i = baseIndex + int.Parse(entry.Id[1..], CultureInfo.InvariantCulture);
                var group = Groups[i % Groups.Length];
                if (!seqs.TryGetValue(group, out var list))
                {
                    seqs[group] = list = [];
                }

                list.Add(entry.SequenceNumber ?? throw new KeyNotFoundException("SequenceNumber"));
            }
        }

        return seqs;
    }

    /// <summary>
    /// Drain with `workers` concurrent receivers; return (arrival_index, group, ordinal) tuples.
    ///
    /// The arrival index lets verdicts order within a group; SQS does not define cross-group order.
    /// </summary>
    private static async Task<List<(int Idx, string Group, int Ordinal)>> Drain(FidelityContext ctx, AmazonSQSClient sqs, string url, int want, int workers = 8)
    {
        List<(int Idx, string Group, int Ordinal)> seen = [];
        var gate = new Lock();
        var clock = Stopwatch.StartNew();
        var deadlineS = BudgetS(ctx, DrainDeadlineS);
        var idx = 0;
        var multiGroupCalls = 0;
        var done = false;

        async Task ReceiveLoop()
        {
            Stopwatch? idleSince = null;
            while (clock.Elapsed.TotalSeconds < deadlineS && !Volatile.Read(ref done))
            {
                var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
                {
                    QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 1,
                    MessageSystemAttributeNames = ["MessageGroupId"],
                });
                var msgs = resp.Messages ?? [];
                if (msgs.Count == 0)
                {
                    if (idleSince is null)
                    {
                        idleSince = Stopwatch.StartNew();
                    }
                    else if (idleSince.Elapsed.TotalSeconds >= DrainIdleS)
                    {
                        return;
                    }

                    continue;
                }

                idleSince = null;
                lock (gate)
                {
                    if (msgs.Select(m => m.Attributes!["MessageGroupId"]).Distinct().Count() > 1)
                    {
                        multiGroupCalls++;
                    }

                    foreach (var m in msgs)
                    {
                        var (group, ordinal) = Parse(m.Body);
                        Ensure(m.Attributes!["MessageGroupId"] == group,
                            $"MessageGroupId system attribute '{m.Attributes["MessageGroupId"]}' " +
                            $"does not match the group encoded in the body '{m.Body}'");
                        seen.Add((idx, group, ordinal));
                        idx++;
                    }
                }

                await sqs.DeleteMessageBatchAsync(new DeleteMessageBatchRequest
                {
                    QueueUrl = url,
                    Entries = msgs.Select((m, i) => new DeleteMessageBatchRequestEntry { Id = $"d{i}", ReceiptHandle = m.ReceiptHandle }).ToList(),
                });
                lock (gate)
                {
                    if (seen.Count >= want)
                    {
                        Volatile.Write(ref done, true);
                    }
                }
            }
        }

        await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => Task.Run(ReceiveLoop)));

        if (seen.Count < want && BudgetSpent(ctx))
        {
            throw new TimeoutException($"draining {want} messages: the run budget ended first; not graded");
        }

        return seen;
    }

    /// <summary>
    /// (count, distinct, per-group order), independent of the order of `observations`.
    ///
    /// Ordering uses each observation's arrival index, so a shuffled copy gives the same verdict.
    /// </summary>
    private static (int Count, int Distinct, SortedDictionary<string, int[]> Ordered) Verdict(List<(int Idx, string Group, int Ordinal)> observations)
    {
        var perGroup = new Dictionary<string, List<(int, int)>>();
        foreach (var (idx, group, ordinal) in observations)
        {
            if (!perGroup.TryGetValue(group, out var list))
            {
                perGroup[group] = list = [];
            }

            list.Add((idx, ordinal));
        }

        var ordered = new SortedDictionary<string, int[]>(StringComparer.Ordinal);
        foreach (var (g, v) in perGroup)
        {
            ordered[g] = [.. v.Order().Select(p => p.Item2)];
        }

        return (
            observations.Count,
            observations.Select(o => (o.Group, o.Ordinal)).Distinct().Count(),
            ordered);
    }

    private static bool VerdictEquals((int Count, int Distinct, SortedDictionary<string, int[]> Ordered) a, (int Count, int Distinct, SortedDictionary<string, int[]> Ordered) b) =>
        a.Count == b.Count && a.Distinct == b.Distinct && OrderedEquals(a.Ordered, b.Ordered);

    private static bool OrderedEquals(IDictionary<string, int[]> a, IDictionary<string, int[]> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v.SequenceEqual(kv.Value));

    private static string Diff(SortedDictionary<string, int[]> ordered, Dictionary<string, int[]> expect) =>
        "{" + string.Join(", ", ordered
            .Where(kv => !(expect.TryGetValue(kv.Key, out var e) && e.SequenceEqual(kv.Value)))
            .Select(kv => $"'{kv.Key}': ({string.Join(", ", kv.Value)})")) + "}";

    private static string Repr(SortedDictionary<string, int[]> ordered) =>
        "{" + string.Join(", ", ordered.Select(kv => $"'{kv.Key}': ({string.Join(", ", kv.Value)})")) + "}";

    private static List<T> Sample<T>(Random rng, List<T> items)
    {
        var copy = items.ToArray();
        rng.Shuffle(copy);
        return [.. copy];
    }

    /// <summary>Poll the queue counters until they reach the expected pair, or fail with what we saw.</summary>
    private static async Task<Dictionary<string, string>> AwaitCounters(FidelityContext ctx, AmazonSQSClient sqs, string url, int visible, int inFlight)
    {
        Dictionary<string, string> attrs = [];
        var deadline = Stopwatch.StartNew();
        var budget = BudgetS(ctx, 60);
        while (deadline.Elapsed.TotalSeconds < budget)
        {
            attrs = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = url,
                AttributeNames = ["ApproximateNumberOfMessages", "ApproximateNumberOfMessagesNotVisible"],
            })).Attributes!;
            if (attrs["ApproximateNumberOfMessages"] == visible.ToString(CultureInfo.InvariantCulture)
                && attrs["ApproximateNumberOfMessagesNotVisible"] == inFlight.ToString(CultureInfo.InvariantCulture))
            {
                return attrs;
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        if (BudgetSpent(ctx))
        {
            throw new TimeoutException(
                $"queue counters reaching visible={visible} in_flight={inFlight}: the run budget ended first; not graded");
        }

        throw Fail(
            $"queue counters never reached visible={visible} in_flight={inFlight}; last read {{{string.Join(", ", attrs.Select(kv => $"'{kv.Key}': '{kv.Value}'"))}}}");
    }

    /// <summary>
    /// Receive one message at a time until `group`'s head arrives; keep it in flight.
    ///
    /// Other messages are returned via ChangeMessageVisibility(0), not deleted, so TOTAL - 1 remain.
    /// </summary>
    private static async Task<Message> HoldGroupHead(FidelityContext ctx, AmazonSQSClient sqs, string url, string group)
    {
        var deadline = Stopwatch.StartNew();
        var budget = BudgetS(ctx, 240.0);
        for (var attempt = 0; attempt < 120; attempt++)
        {
            if (deadline.Elapsed.TotalSeconds >= budget)
            {
                break;
            }

            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 2,
                MessageSystemAttributeNames = ["MessageGroupId"],
            });
            var msgs = resp.Messages ?? [];
            if (msgs.Count == 0)
            {
                continue;
            }

            var msg = msgs[0];
            var (gotGroup, ordinal) = Parse(msg.Body);
            if (gotGroup == group)
            {
                Ensure(ordinal == 0,
                    $"the first message ever delivered for group '{group}' was ordinal {ordinal}, " +
                    "not its head (0) — per-group FIFO order was violated before the stall began");
                return msg;
            }

            await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest
            {
                QueueUrl = url, ReceiptHandle = msg.ReceiptHandle, VisibilityTimeout = 0,
            });
        }

        if (BudgetSpent(ctx))
        {
            throw new TimeoutException($"receiving group '{group}''s head: the run budget ended first; not graded");
        }

        throw Fail($"never received a message for group '{group}' in up to 120 single receives");
    }
}

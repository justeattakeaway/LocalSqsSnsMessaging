// Translated from behavioral/sqs/check_fifo_per_group_order_preserved_under_concurrent_receivers.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using System.Globalization;
using Amazon.Runtime;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.fifo-per-group-order-preserved-under-concurrent-receivers</summary>
[Category("AwsEvaluationSuite")]
public sealed class FifoPerGroupOrderPreservedUnderConcurrentReceivers
{
    private static readonly Dictionary<string, string> Tags = new()
    {
        ["sim2real:managed"] = "true",
        ["sim2real"] = "fifo-order-under-concurrency",
    };

    private const int Groups = 3;
    private const int PerGroup = 6;
    private const int Total = Groups * PerGroup;
    private const int Workers = 4;
    // VisibilityTimeout must be comfortably LONGER than the drain deadline. Every worker deletes each
    // message the moment it has recorded it, so a redelivery can only happen if a worker stalls for
    // the whole visibility window -- and a redelivery would be a legitimate SQS behaviour that the
    // "no message twice" assertion below would misread as a defect. Keeping the deadline under the
    // timeout is what makes that assertion an invariant rather than a race.
    private const int VisibilityS = 120;
    private const int DrainDeadlineS = 60;

    /// <summary>
    /// Four concurrent receivers draining a FIFO queue of three groups see each group in send order
    /// and no message twice; group blocking is what makes FIFO usable by a consumer fleet.
    ///
    /// Reference: https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/FIFO-queues-understanding-logic.html
    /// </summary>
    [Test]
    [DisplayName("sqs.fifo-per-group-order-preserved-under-concurrent-receivers")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        // No exactly-once or per-worker distribution claim: one worker receiving all 18 is faithful.
        var sqs = ctx.Sqs;
        var token = Guid.NewGuid().ToString("N")[..8];

        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-") + ".fifo",
            Attributes = new()
            {
                ["FifoQueue"] = "true", ["ContentBasedDeduplication"] = "true",
                ["VisibilityTimeout"] = VisibilityS.ToString(CultureInfo.InvariantCulture),
            },
            Tags = new(Tags),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var groups = Enumerable.Range(0, Groups).Select(i => $"g{i}-{token}").ToList();
        // Sent sequentially, per group, so the intended order is unambiguous. SequenceNumbers are
        // recorded per group and asserted strictly increasing, which is what pins "send order" to
        // something the service itself reports rather than to this loop's own ordering.
        var sentSeqs = groups.ToDictionary(g => g, _ => new List<string>());
        for (var idx = 0; idx < PerGroup; idx++)
        {
            foreach (var g in groups)
            {
                var r = await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = $"{g}|{idx}", MessageGroupId = g });
                sentSeqs[g].Add(r.SequenceNumber ?? throw new KeyNotFoundException("SequenceNumber"));
            }
        }

        foreach (var g in groups)
        {
            Ensure(sentSeqs[g].SequenceEqual(sentSeqs[g].Order(StringComparer.Ordinal)) && sentSeqs[g].Distinct().Count() == PerGroup,
                $"group '{g}' SequenceNumbers are not strictly increasing in send order, so 'send " +
                $"order' is undefined below: [{string.Join(", ", sentSeqs[g])}]");
        }

        var gate = new Lock();
        List<(int Worker, string Group, int Index)> collected = [];
        List<string> errors = [];
        var clock = Stopwatch.StartNew();
        var deadline = TimeSpan.FromSeconds(DrainDeadlineS);

        async Task Worker(int wid)
        {
            while (clock.Elapsed < deadline)
            {
                lock (gate)
                {
                    if (collected.Count >= Total)
                    {
                        return;
                    }
                }

                List<Message> batch;
                try
                {
                    batch = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
                    {
                        QueueUrl = url, MaxNumberOfMessages = 2, WaitTimeSeconds = 5,
                    })).Messages ?? [];
                }
                catch (AmazonServiceException exc)
                {
                    lock (gate)
                    {
                        errors.Add(string.IsNullOrEmpty(exc.ErrorCode) ? "?" : exc.ErrorCode);
                    }

                    return;
                }

                if (batch.Count == 0)
                {
                    continue;
                }

                // Record the whole batch UNDER THE LOCK, then delete. The order matters: a group stays
                // blocked until its in-flight message is deleted, so recording before deleting is what
                // guarantees that `collected` observes each group in delivery order. Deleting first
                // would let another worker receive and record the group's NEXT message before this
                // worker had recorded the current one -- a bookkeeping race that would read as a FIFO
                // ordering violation.
                lock (gate)
                {
                    foreach (var m in batch)
                    {
                        var sep = m.Body.IndexOf('|', StringComparison.Ordinal);
                        var (g, i) = sep < 0 ? (m.Body, "") : (m.Body[..sep], m.Body[(sep + 1)..]);
                        collected.Add((wid, g, int.Parse(i, CultureInfo.InvariantCulture)));
                    }
                }

                foreach (var m in batch)
                {
                    try
                    {
                        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle });
                    }
                    catch (AmazonServiceException exc)
                    {
                        lock (gate)
                        {
                            errors.Add(string.IsNullOrEmpty(exc.ErrorCode) ? "?" : exc.ErrorCode);
                        }
                    }
                }
            }
        }

        // wait for every worker so none is still calling receive_message against a queue the
        // cleanup registered above is about to delete.
        await Task.WhenAll(Enumerable.Range(0, Workers).Select(w => Task.Run(() => Worker(w))));

        Ensure(errors.Count == 0, $"concurrent receivers raised: [{string.Join(", ", errors)}]");
        Ensure(collected.Count == Total,
            $"the concurrent receivers drained {collected.Count} of {Total} messages before the " +
            $"{DrainDeadlineS}s deadline: [{string.Join(", ", collected.Select(c => (c.Group, c.Index)).Order())}]");
        Ensure(collected.Select(c => (c.Group, c.Index)).Distinct().Count() == Total,
            "a FIFO message was delivered to more than one concurrent receiver: " +
            $"[{string.Join(", ", collected.Order())}]");

        var perGroupOrder = groups.ToDictionary(g => g, _ => new List<int>());
        foreach (var (_, g, i) in collected)
        {
            Ensure(perGroupOrder.ContainsKey(g), $"a message arrived from an unknown group '{g}'");
            perGroupOrder[g].Add(i);
        }

        foreach (var (g, order) in perGroupOrder)
        {
            Ensure(order.SequenceEqual(order.Order()),
                $"per-group FIFO order was violated under {Workers} concurrent receivers: group " +
                $"'{g}' was observed as [{string.Join(", ", order)}], expected [{string.Join(", ", order.Order())}]");
            Ensure(order.SequenceEqual(Enumerable.Range(0, PerGroup)),
                $"group '{g}' did not deliver all {PerGroup} of its messages exactly once: " +
                $"[{string.Join(", ", order)}]");
        }
    }
}

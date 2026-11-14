// Translated from behavioral/sqs/check_fifo_sequence_number_monotonic_within_group.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Numerics;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.fifo-sequence-number-monotonic-within-group</summary>
[Category("AwsEvaluationSuite")]
public sealed class FifoSequenceNumberMonotonicWithinGroup
{
    private static readonly Dictionary<string, string> Tags = new()
    {
        ["sim2real:managed"] = "true",
        ["sim2real"] = "fifo-sequence-number-monotonic-within-group",
    };

    /// <summary>
    /// FIFO SequenceNumber strictly increases with send order within each MessageGroupId.
    ///
    /// The number SendMessage echoes is the one on the received message; delivery keeps group order.
    /// </summary>
    [Test]
    [DisplayName("sqs.fifo-sequence-number-monotonic-within-group")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-") + ".fifo",
            Attributes = new() { ["FifoQueue"] = "true" },
            Tags = new(Tags),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var token = Guid.NewGuid().ToString("N")[..8];
        // Interleaved sends across two groups, so a per-group sequence cannot be faked by
        // replaying the global send order.
        (string Group, int Idx)[] plan = [("A", 0), ("B", 0), ("A", 1), ("A", 2), ("B", 1), ("A", 3)];
        List<(string Group, string Body, string Seq)> sent = []; // (group, body, sequence_number) in send order
        foreach (var (group, idx) in plan)
        {
            var body = $"seq-{group}-{idx}-{token}";
            var resp = await sqs.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = url,
                MessageBody = body,
                MessageGroupId = $"grp-{group}-{token}",
                MessageDeduplicationId = $"{token}-{group}-{idx}",
            });
            var seq = resp.SequenceNumber;
            Ensure(!string.IsNullOrEmpty(seq) && seq.All(char.IsDigit), $"SendMessage returned no numeric SequenceNumber: '{seq}'");
            sent.Add((group, body, seq));
        }

        foreach (var group in new[] { "A", "B" })
        {
            var seqs = sent.Where(s => s.Group == group).Select(s => BigInteger.Parse(s.Seq, CultureInfo.InvariantCulture)).ToList();
            Ensure(seqs.SequenceEqual(seqs.Order()) && seqs.Distinct().Count() == seqs.Count,
                $"group {group} SequenceNumbers are not strictly increasing in send order: [{string.Join(", ", seqs)}]");
        }

        var allSeqs = sent.Select(s => s.Seq).ToList();
        Ensure(allSeqs.Distinct().Count() == allSeqs.Count, $"SequenceNumber reused across the queue: [{string.Join(", ", allSeqs)}]");

        var received = await ReceiveAll(sqs, url, expect: plan.Length, timeoutS: 45);
        Ensure(received.Count == plan.Length,
            $"expected {plan.Length} messages, received {received.Count}: " +
            $"[{string.Join(", ", received.Select(m => m.Body))}]");
        var byBody = new Dictionary<string, string>();
        foreach (var msg in received)
        {
            var attrs = msg.Attributes ?? [];
            Ensure(attrs.ContainsKey("SequenceNumber"),
                $"received message carries no SequenceNumber system attribute: [{string.Join(", ", attrs.Keys.Order(StringComparer.Ordinal))}]");
            byBody[msg.Body] = attrs["SequenceNumber"];
        }

        foreach (var (_, body, seq) in sent)
        {
            Ensure(byBody.GetValueOrDefault(body) == seq,
                $"{body}: SendMessage echoed SequenceNumber {seq} but the received message " +
                $"carries '{byBody.GetValueOrDefault(body)}'");
        }

        foreach (var group in new[] { "A", "B" })
        {
            var want = sent.Where(s => s.Group == group).Select(s => s.Body).ToList();
            var wantSet = want.ToHashSet();
            var got = received.Select(m => m.Body).Where(wantSet.Contains).ToList();
            Ensure(got.SequenceEqual(want), $"group {group} delivery order [{string.Join(", ", got)}] != send order [{string.Join(", ", want)}]");
        }
    }

    /// <summary>Receive up to `expect` distinct messages within timeout_s, preserving arrival order.</summary>
    private static async Task<List<Message>> ReceiveAll(AmazonSQSClient sqs, string url, int expect, double timeoutS, bool delete = true)
    {
        HashSet<string> seen = [];
        List<Message> msgs = [];
        var deadline = FidelityTime.StartNew();
        while (msgs.Count < expect && deadline.Elapsed.TotalSeconds < timeoutS)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 1,
                MessageSystemAttributeNames = ["SequenceNumber", "MessageGroupId"],
            });
            foreach (var msg in resp.Messages ?? [])
            {
                if (seen.Add(msg.MessageId))
                {
                    msgs.Add(msg);
                }

                if (delete)
                {
                    await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle });
                }
            }
        }

        return msgs;
    }
}

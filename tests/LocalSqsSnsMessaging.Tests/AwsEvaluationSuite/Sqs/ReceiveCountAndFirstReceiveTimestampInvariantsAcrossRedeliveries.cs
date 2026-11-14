// Translated from behavioral/sqs/check_receive_count_and_first_receive_timestamp_invariants_across_redeliveries.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.receive-count-and-first-receive-timestamp-invariants-across-redeliveries</summary>
[Category("AwsEvaluationSuite")]
public sealed class ReceiveCountAndFirstReceiveTimestampInvariantsAcrossRedeliveries
{
    private const int Deliveries = 3;

    /// <summary>
    /// Across three redeliveries of one message, ApproximateReceiveCount goes 1 -> 2 -> 3 while
    /// SentTimestamp and ApproximateFirstReceiveTimestamp stay identical, with SentTimestamp first.
    ///
    /// Reference: https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_ReceiveMessage.html
    /// </summary>
    [Test]
    [DisplayName("sqs.receive-count-and-first-receive-timestamp-invariants-across-redeliveries")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var marker = $"redelivery-marker-{Guid.NewGuid().ToString("N")[..8]}";

        // VisibilityTimeout=2 so a redelivery is a couple of seconds away rather than minutes.
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-"),
            Attributes = new() { ["VisibilityTimeout"] = "2" },
            Tags = new() { ["sim2real:managed"] = "true", ["sim2real"] = "receive-count-timestamp-invariants" },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var sent = await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = marker });
        var sentId = sent.MessageId;

        // The message is never deleted, so each visibility-timeout expiry redelivers it.
        List<Message> deliveries = [];
        var deadline = FidelityTime.StartNew();
        while (deliveries.Count < Deliveries && deadline.Elapsed < TimeSpan.FromSeconds(120))
        {
            var batch = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url,
                MaxNumberOfMessages = 1,
                WaitTimeSeconds = 2,
                AttributeNames = ["All"],
                MessageSystemAttributeNames = ["All"],
            })).Messages ?? [];
            deliveries.AddRange(batch);
        }

        Ensure(deliveries.Count == Deliveries,
            $"only {deliveries.Count} of {Deliveries} deliveries of the same message arrived before "
            + "the deadline -- a message left un-deleted past its 2s VisibilityTimeout must be "
            + $"redelivered: got [{string.Join(", ", deliveries.Select(d => d.Attributes?.GetValueOrDefault("ApproximateReceiveCount") ?? "None"))}]");

        Ensure(deliveries.All(d => d.MessageId == sentId),
            $"the three deliveries were not all the message that was sent ('{sentId}'): "
            + $"[{string.Join(", ", deliveries.Select(d => d.MessageId))}]");

        // ApproximateReceiveCount is documented as approximate; with one producer and one consumer it
        // is exact, so a monotonic but non-consecutive sequence fails on purpose.
        var counts = deliveries.Select(d => int.Parse(d.Attributes!["ApproximateReceiveCount"])).ToList();
        Ensure(counts.SequenceEqual([1, 2, 3]),
            $"ApproximateReceiveCount must increment exactly once per delivery: got [{string.Join(", ", counts)}], "
            + "expected [1, 2, 3]");

        var sentStamps = deliveries.Select(d => d.Attributes!["SentTimestamp"]).ToHashSet();
        Ensure(sentStamps.Count == 1,
            "SentTimestamp changed between deliveries of the SAME message -- it records the enqueue "
            + $"instant and cannot move: [{string.Join(", ", deliveries.Select(d => d.Attributes!["SentTimestamp"]))}]");

        var firstStamps = deliveries.Select(d => d.Attributes!["ApproximateFirstReceiveTimestamp"]).ToHashSet();
        Ensure(firstStamps.Count == 1,
            "ApproximateFirstReceiveTimestamp must record the FIRST receive and never move: "
            + $"[{string.Join(", ", deliveries.Select(d => d.Attributes!["ApproximateFirstReceiveTimestamp"]))}]");

        var sentMs = long.Parse(sentStamps.Single());
        var firstRecvMs = long.Parse(firstStamps.Single());
        Ensure(sentMs <= firstRecvMs,
            $"ApproximateFirstReceiveTimestamp ({firstRecvMs}) precedes SentTimestamp ({sentMs}) "
            + "-- a message cannot be received before it was sent");

        // Both are epoch milliseconds; a seconds value would pass every assertion above.
        Ensure(sentMs.ToString().Length == 13,
            $"SentTimestamp must be epoch milliseconds; {sentMs} has {sentMs.ToString().Length} digits");
        Ensure(firstRecvMs.ToString().Length == 13,
            $"ApproximateFirstReceiveTimestamp must be epoch milliseconds; {firstRecvMs} has "
            + $"{firstRecvMs.ToString().Length} digits");
    }
}

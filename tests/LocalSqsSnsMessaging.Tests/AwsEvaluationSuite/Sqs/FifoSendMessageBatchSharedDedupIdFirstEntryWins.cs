// Translated from behavioral/sqs/check_fifo_send_message_batch_shared_dedup_id_first_entry_wins.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.fifo-send-message-batch-shared-dedup-id-first-entry-wins</summary>
[Category("AwsEvaluationSuite")]
public sealed class FifoSendMessageBatchSharedDedupIdFirstEntryWins
{
    private static readonly Dictionary<string, string> Tags = new()
    {
        ["sim2real"] = "fifo-send-message-batch-shared-dedup-id-first-entry-wins",
    };

    /// <summary>
    /// Two batch entries sharing a dedup id both succeed with one MessageId and SequenceNumber.
    ///
    /// Each keeps its own MD5OfMessageBody, but only the FIRST entry by position is delivered.
    /// </summary>
    [Test]
    [DisplayName("sqs.fifo-send-message-batch-shared-dedup-id-first-entry-wins")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-fifodup-") + ".fifo";
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = name,
            Attributes = new() { ["FifoQueue"] = "true", ["ContentBasedDeduplication"] = "false" },
            Tags = new(Tags),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var dedupId = Guid.NewGuid().ToString("N");
        var resp = await sqs.SendMessageBatchAsync(new SendMessageBatchRequest
        {
            QueueUrl = url,
            Entries =
            [
                new SendMessageBatchRequestEntry
                {
                    Id = "z-later-id", MessageBody = "first-position-body",
                    MessageGroupId = "g1", MessageDeduplicationId = dedupId,
                },
                new SendMessageBatchRequestEntry
                {
                    Id = "a-earlier-id", MessageBody = "second-position-body",
                    MessageGroupId = "g1", MessageDeduplicationId = dedupId,
                },
            ],
        });
        Ensure(resp.Failed is null or { Count: 0 },
            $"a batch with two entries sharing one dedup id answered Failed={FailedRepr(resp.Failed)}");
        var successful = new Dictionary<string, SendMessageBatchResultEntry>();
        foreach (var e in resp.Successful ?? [])
        {
            successful[e.Id] = e;
        }

        Ensure(successful.Keys.ToHashSet().SetEquals(["z-later-id", "a-earlier-id"]),
            $"Successful Ids were [{string.Join(", ", successful.Keys.Order(StringComparer.Ordinal))}], expected both entries");
        var messageIds = successful.Values.Select(e => e.MessageId).ToHashSet();
        var sequenceNumbers = successful.Values.Select(e => e.SequenceNumber ?? throw new KeyNotFoundException("SequenceNumber")).ToHashSet();
        Ensure(messageIds.Count == 1,
            $"the two entries minted distinct MessageIds {{{string.Join(", ", messageIds)}}}; sharing a dedup id in " +
            "one batch call must resolve to the SAME message");
        Ensure(sequenceNumbers.Count == 1,
            $"the two entries minted distinct SequenceNumbers {{{string.Join(", ", sequenceNumbers)}}}; they must share " +
            "the one FIFO sequence position their shared dedup id resolved to");
        var md5s = successful.ToDictionary(kv => kv.Key, kv => kv.Value.MD5OfMessageBody);
        Ensure(md5s["z-later-id"] != md5s["a-earlier-id"],
            $"MD5OfMessageBody was identical for both entries ({string.Join(", ", md5s)}); each entry's digest must " +
            "reflect its OWN body, even though only one body is ever delivered");

        var delivered = await Drain(sqs, url, minCount: 1);
        var bodies = delivered.Select(m => m.Body).ToList();
        Ensure(bodies.SequenceEqual(["first-position-body"]),
            $"the queue delivered [{string.Join(", ", bodies)}]; a shared dedup id inside one batch call must resolve " +
            "to the FIRST entry BY POSITION ('first-position-body'), not the second, and not both");

        // A fresh dedup id is not swallowed: the dedup window is scoped to the id, not the batch.
        var other = await sqs.SendMessageBatchAsync(new SendMessageBatchRequest
        {
            QueueUrl = url,
            Entries =
            [
                new SendMessageBatchRequestEntry
                {
                    Id = "ctrl", MessageBody = "control-body", MessageGroupId = "g1",
                    MessageDeduplicationId = Guid.NewGuid().ToString("N"),
                },
            ],
        });
        Ensure(other.Failed is null or { Count: 0 }, $"control send answered Failed={FailedRepr(other.Failed)}");
        var controlDelivered = await Drain(sqs, url, minCount: 1);
        Ensure(controlDelivered.Select(m => m.Body).SequenceEqual(["control-body"]),
            $"the control send delivered [{string.Join(", ", controlDelivered.Select(m => m.Body))}], expected " +
            "exactly ['control-body']");
    }

    private static async Task<List<Message>> Drain(AmazonSQSClient sqs, string url, int minCount, double timeoutS = 20.0)
    {
        List<Message> seen = [];
        var deadline = FidelityTime.StartNew();
        var idle = 0;
        while (deadline.Elapsed.TotalSeconds < timeoutS)
        {
            var msgs = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 })).Messages ?? [];
            if (msgs.Count == 0)
            {
                idle++;
                if (seen.Count >= minCount && idle >= 2)
                {
                    break;
                }

                continue;
            }

            idle = 0;
            foreach (var m in msgs)
            {
                seen.Add(m);
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle });
            }
        }

        return seen;
    }

    private static string FailedRepr(List<BatchResultErrorEntry>? failed) =>
        failed is null ? "None" : "[" + string.Join(", ", failed.Select(f => $"{{Id: {f.Id}, Code: {f.Code}, Message: {f.Message}}}")) + "]";
}

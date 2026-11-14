// Translated from behavioral/sqs/check_batch_entry_over_queue_max_message_size_fails_alone.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using System.Globalization;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.batch-entry-over-queue-max-message-size-fails-alone</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchEntryOverQueueMaxMessageSizeFailsAlone
{
    private const int Limit = 1024;
    private const string EntryOversizeMessage =
        "One or more parameters cannot be validated. Reason: Message must be shorter than 1024 bytes.";

    /// <summary>
    /// A queue's MaximumMessageSize bounds each SendMessageBatch entry, not the batch: on a 1024-byte
    /// queue a 1025-byte entry fails alone while its siblings land, and two 1000-byte entries (2000
    /// bytes in total) are accepted whole.
    /// </summary>
    [Test]
    [DisplayName("sqs.batch-entry-over-queue-max-message-size-fails-alone")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-batchsize-"),
            Attributes = new() { ["MaximumMessageSize"] = Limit.ToString(CultureInfo.InvariantCulture) },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var limit = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["MaximumMessageSize"] }))
            .Attributes!["MaximumMessageSize"];
        Ensure(limit == Limit.ToString(CultureInfo.InvariantCulture), $"MaximumMessageSize read back as '{limit}', expected '{Limit}'");

        var mixed = await sqs.SendMessageBatchAsync(new SendMessageBatchRequest
        {
            QueueUrl = url,
            Entries =
            [
                new SendMessageBatchRequestEntry { Id = "fits", MessageBody = new string('a', Limit) },
                new SendMessageBatchRequestEntry { Id = "oversize", MessageBody = new string('b', Limit + 1) },
            ],
        });
        var okIds = (mixed.Successful ?? []).Select(e => e.Id).ToList();
        var failed = (mixed.Failed ?? []).ToDictionary(e => e.Id);
        Ensure(okIds.SequenceEqual(["fits"]) && failed.Keys.Order(StringComparer.Ordinal).SequenceEqual(["oversize"]),
            $"expected 'fits' to succeed and only 'oversize' to fail; Successful ids [{string.Join(", ", okIds)}], " +
            $"Failed {(mixed.Failed is null ? "None" : $"[{string.Join(", ", mixed.Failed.Select(f => f.Id))}]")}");
        var bad = failed["oversize"];
        Ensure((bad.Code, bad.SenderFault, bad.Message) == ("InvalidParameterValue", true, EntryOversizeMessage),
            $"the oversize entry failed with ({bad.Code}, {bad.SenderFault}, {bad.Message})");

        var whole = await sqs.SendMessageBatchAsync(new SendMessageBatchRequest
        {
            QueueUrl = url,
            Entries =
            [
                new SendMessageBatchRequestEntry { Id = "c", MessageBody = new string('c', 1000) },
                new SendMessageBatchRequestEntry { Id = "d", MessageBody = new string('d', 1000) },
            ],
        });
        Ensure((whole.Successful ?? []).Select(e => e.Id).Order(StringComparer.Ordinal).SequenceEqual(["c", "d"]) && whole.Failed is null or { Count: 0 },
            $"a batch whose entries each fit but whose total (2000 bytes) exceeds the queue's " +
            $"{Limit}-byte MaximumMessageSize must be accepted whole; Successful " +
            $"[{string.Join(", ", (whole.Successful ?? []).Select(e => e.Id))}], Failed [{string.Join(", ", (whole.Failed ?? []).Select(e => e.Id))}]");

        var got = new List<(string, int)>();
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            var msgs = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 })).Messages ?? [];
            if (msgs.Count == 0 && got.Count >= 3)
            {
                break;
            }

            foreach (var m in msgs)
            {
                got.Add((m.Body[..1], m.Body.Length));
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle });
            }
        }

        var sortedGot = got.Order().ToList();
        Ensure(sortedGot.SequenceEqual([("a", Limit), ("c", 1000), ("d", 1000)]),
            $"the queue must hold exactly the three accepted bodies and not the oversize one; " +
            $"received (first char, length) [{string.Join(", ", sortedGot)}]");
    }
}

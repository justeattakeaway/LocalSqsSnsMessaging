// Translated from behavioral/sqs/check_fifo_dedup_replay_pins_original_payload_then_fresh_id_advances.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Amazon.SQS;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.fifo-dedup-replay-pins-original-payload-then-fresh-id-advances</summary>
[Category("AwsEvaluationSuite")]
public sealed class FifoDedupReplayPinsOriginalPayloadThenFreshIdAdvances
{
    /// <summary>
    /// A FIFO dedup replay against a still-queued message echoes the first send's MessageId and
    /// SequenceNumber with the replayed body's own MD5, and delivery still carries only the first
    /// send's payload. A fresh deduplication id then mints a new, greater SequenceNumber and delivers.
    /// </summary>
    [Test]
    [DisplayName("sqs.fifo-dedup-replay-pins-original-payload-then-fresh-id-advances")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-dedupreplay-") + ".fifo",
            Attributes = new() { ["FifoQueue"] = "true" },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        const string group = "orders";
        const string dedup1 = "order-7001";
        const string body1 = "charge-order-7001-v1";
        const string body2 = "charge-order-7001-v2";

        var first = await sqs.SendMessageAsync(Send(url, body1, group, dedup1, "pending"));
        var second = await sqs.SendMessageAsync(Send(url, body2, group, dedup1, "shipped"));
        Ensure(second.MessageId == first.MessageId,
            $"dedup replay MessageId '{second.MessageId}' != original '{first.MessageId}'");
        Ensure(second.SequenceNumber == first.SequenceNumber,
            $"dedup replay SequenceNumber '{second.SequenceNumber}' != original '{first.SequenceNumber}'");
        var expectedMd52 = Md5(body2);
        Ensure(second.MD5OfMessageBody == expectedMd52,
            $"dedup replay MD5OfMessageBody '{second.MD5OfMessageBody}' != md5 of the replayed body '{expectedMd52}'");

        var delivered = await Drain(sqs, url, maxMessages: 1);
        Ensure(delivered.Count == 1, $"expected exactly 1 delivered message, got {delivered.Count}");
        var msg = delivered[0];
        var expectedMd51 = Md5(body1);
        Ensure(msg.MessageId == first.MessageId, $"delivered MessageId '{msg.MessageId}' != first send's '{first.MessageId}'");
        Ensure(msg.Body == body1, $"delivered body '{msg.Body}' != first send's body '{body1}'");
        Ensure(msg.MD5OfBody == expectedMd51, $"delivered MD5OfBody '{msg.MD5OfBody}' != md5 of the first send's body '{expectedMd51}'");
        var status = msg.MessageAttributes?.GetValueOrDefault("status")?.StringValue;
        Ensure(status == "pending",
            $"delivered attribute status='{status}' != first send's 'pending' " +
            "(the replay's attribute must never overwrite the stored message)");
        await sqs.DeleteMessageAsync(url, msg.ReceiptHandle);

        const string dedup3 = "order-7002";
        const string body3 = "charge-order-7002-v1";
        var third = await sqs.SendMessageAsync(Send(url, body3, group, dedup3, "pending"));
        Ensure(third.MessageId != first.MessageId,
            "a fresh deduplication id must mint a new MessageId, but it echoed the first send's");
        Ensure(BigInteger.Parse(third.SequenceNumber) > BigInteger.Parse(first.SequenceNumber),
            $"a fresh deduplication id must get a greater SequenceNumber: '{third.SequenceNumber}' " +
            $"is not greater than '{first.SequenceNumber}'");

        var afterDelete = await Drain(sqs, url, maxMessages: 1);
        Ensure(afterDelete.Count == 1,
            "after deleting the first message, expected only the fresh deduplication id's message " +
            $"to be delivered, got {afterDelete.Count}");
        var freshMsg = afterDelete[0];
        Ensure(freshMsg.MessageId == third.MessageId,
            $"delivered MessageId '{freshMsg.MessageId}' != fresh send's '{third.MessageId}' " +
            "(no leftover message from the deleted dedup group was still queued)");
        Ensure(freshMsg.Body == body3, $"delivered body '{freshMsg.Body}' != fresh send's body '{body3}'");
        await sqs.DeleteMessageAsync(url, freshMsg.ReceiptHandle);
    }

    private static SendMessageRequest Send(string url, string body, string group, string dedup, string status) => new()
    {
        QueueUrl = url,
        MessageBody = body,
        MessageGroupId = group,
        MessageDeduplicationId = dedup,
        MessageAttributes = new() { ["status"] = new MessageAttributeValue { DataType = "String", StringValue = status } },
    };

    private static string Md5(string s) => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(s)));

    /// <summary>Unique messages seen over repeated receives, stopping after <paramref name="idleRounds"/> empty polls.</summary>
    private static async Task<List<Message>> Drain(AmazonSQSClient sqs, string url, int maxMessages, int idleRounds = 3)
    {
        var seenIds = new HashSet<string>();
        var messages = new List<Message>();
        var idle = 0;
        while (true)
        {
            var msgs = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2, MessageAttributeNames = ["All"],
            })).Messages ?? [];
            if (msgs.Count == 0)
            {
                idle++;
                if (idle >= idleRounds)
                {
                    return messages;
                }

                continue;
            }

            idle = 0;
            foreach (var m in msgs)
            {
                if (seenIds.Add(m.MessageId))
                {
                    messages.Add(m);
                }
            }

            if (messages.Count >= maxMessages)
            {
                return messages;
            }
        }
    }
}

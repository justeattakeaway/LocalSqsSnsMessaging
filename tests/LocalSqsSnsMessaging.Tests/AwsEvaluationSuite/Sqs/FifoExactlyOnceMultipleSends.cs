// Translated from behavioral/sqs/check_fifo_exactly_once_multiple_sends.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.fifo-exactly-once-multiple-sends</summary>
[Category("AwsEvaluationSuite")]
public sealed class FifoExactlyOnceMultipleSends
{
    /// <summary>
    /// Send same dedup ID 5 times rapidly. Every send must report the same MessageId, and exactly 1 message is ever delivered.
    /// </summary>
    [Test]
    [DisplayName("sqs.fifo-exactly-once-multiple-sends")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-") + ".fifo",
            Attributes = new() { ["FifoQueue"] = "true" },
            Tags = new() { ["sim2real-behav"] = "fifo-exactly-once-multiple-sends" },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var token = Guid.NewGuid().ToString("N")[..8];
        const string group = "sim2real-group";
        var dedupId = $"{token}-multi-dedup";
        var body = $"fifo-multi-{token}";
        var sendIds = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            var sendResp = await sqs.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = url,
                MessageBody = body,
                MessageGroupId = group,
                MessageDeduplicationId = dedupId,
            });
            sendIds.Add(sendResp.MessageId);
        }

        Ensure(sendIds.Distinct().Count() == 1,
            $"expected all 5 duplicate sends to report the same MessageId, got [{string.Join(", ", sendIds.Distinct().Order(StringComparer.Ordinal))}]");
        var received = new List<Message>();
        var seen = new HashSet<string>();
        var idle = 0;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 1 });
            var msgs = resp.Messages ?? [];
            if (msgs.Count == 0)
            {
                idle++;
                if (received.Count > 0 && idle >= 3)
                {
                    break;
                }

                continue;
            }

            idle = 0;
            foreach (var m in msgs)
            {
                if (seen.Add(m.MessageId))
                {
                    received.Add(m);
                }

                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle });
            }

            if (received.Count >= 5)
            {
                break;
            }
        }

        Ensure(received.Count == 1, $"expected 1 message after 5 dedup sends, got {received.Count}");
        Ensure(received[0].MessageId == sendIds[0],
            $"delivered MessageId {received[0].MessageId} does not match the id " +
            $"SendMessage reported for the deduplicated group, {sendIds[0]}");
    }
}

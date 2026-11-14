// Translated from behavioral/sqs/check_fifo_dedup.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.fifo-dedup</summary>
[Category("AwsEvaluationSuite")]
public sealed class FifoDedup
{
    /// <summary>
    /// Two sends with same MessageDeduplicationId yield ONE message.
    /// </summary>
    [Test]
    [DisplayName("sqs.fifo-dedup")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-") + ".fifo",
            Attributes = new() { ["FifoQueue"] = "true" },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var token = Guid.NewGuid().ToString("N")[..8];
        const string group = "sim2real-group";
        var dedupId = $"{token}-dedup";
        for (var i = 0; i < 2; i++)
        {
            await sqs.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = url,
                MessageBody = $"fifo-dedup-{token}",
                MessageGroupId = group,
                MessageDeduplicationId = dedupId,
            });
        }

        var received = new List<Message>();
        var seen = new HashSet<string>();
        var idle = 0;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 1 });
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

            if (received.Count >= 2)
            {
                break;
            }
        }

        Ensure(received.Count == 1, $"expected 1 after dedup, got {received.Count}");
    }
}

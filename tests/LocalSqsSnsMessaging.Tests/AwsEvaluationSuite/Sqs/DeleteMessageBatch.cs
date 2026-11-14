// Translated from behavioral/sqs/check_delete_message_batch.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.delete-message-batch</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteMessageBatch
{
    /// <summary>
    /// DeleteMessageBatch removes multiple messages in one call; queue ends empty.
    /// </summary>
    [Test]
    [DisplayName("sqs.delete-message-batch")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var token = Guid.NewGuid().ToString("N")[..8];
        var entries = Enumerable.Range(1, 3).Select(i => new SendMessageBatchRequestEntry { Id = i.ToString(), MessageBody = $"delbatch-{i}-{token}" }).ToList();
        await sqs.SendMessageBatchAsync(new SendMessageBatchRequest { QueueUrl = url, Entries = entries });
        var received = new List<Message>();
        var seen = new HashSet<string>();
        var idle = 0;
        for (var attempt = 0; attempt < 15; attempt++)
        {
            var resp0 = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 1 });
            var msgs = resp0.Messages ?? [];
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
            }

            if (received.Count >= 3)
            {
                break;
            }
        }

        if (received.Count != 3)
        {
            throw Fail($"expected 3 messages to delete, got {received.Count}");
        }

        var delEntries = received.Select((m, i) => new DeleteMessageBatchRequestEntry { Id = i.ToString(), ReceiptHandle = m.ReceiptHandle }).ToList();
        var resp = await sqs.DeleteMessageBatchAsync(new DeleteMessageBatchRequest { QueueUrl = url, Entries = delEntries });
        if (resp.Failed is { Count: > 0 })
        {
            throw Fail($"DeleteMessageBatch failures: {JsonSerializer.Serialize(resp.Failed)}");
        }

        var leftover = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 1 })).Messages ?? [];
        Ensure(leftover.Count == 0, $"expected no messages, got {leftover.Count}");
    }
}

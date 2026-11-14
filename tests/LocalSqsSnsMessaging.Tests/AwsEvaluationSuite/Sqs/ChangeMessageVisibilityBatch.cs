// Translated from behavioral/sqs/check_change_message_visibility_batch.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.change-message-visibility-batch</summary>
[Category("AwsEvaluationSuite")]
public sealed class ChangeMessageVisibilityBatch
{
    /// <summary>
    /// ChangeMessageVisibilityBatch(VT=0) re-exposes multiple in-flight messages at once.
    /// </summary>
    [Test]
    [DisplayName("sqs.change-message-visibility-batch")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var token = Guid.NewGuid().ToString("N")[..8];
        var entries = Enumerable.Range(1, 2)
            .Select(i => new SendMessageBatchRequestEntry { Id = i.ToString(CultureInfo.InvariantCulture), MessageBody = $"cmvbatch-{i}-{token}" })
            .ToList();
        await sqs.SendMessageBatchAsync(new SendMessageBatchRequest { QueueUrl = url, Entries = entries });
        var received = new List<Message>();
        var seen = new HashSet<string>();
        var idle = 0;
        for (var attempt = 0; attempt < 15; attempt++)
        {
            var r = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 1 });
            var msgs = r.Messages ?? [];
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

            if (received.Count >= 2)
            {
                break;
            }
        }

        Ensure(received.Count == 2, $"expected 2 messages, got {received.Count}");
        var cmvEntries = received
            .Select((m, i) => new ChangeMessageVisibilityBatchRequestEntry { Id = i.ToString(CultureInfo.InvariantCulture), ReceiptHandle = m.ReceiptHandle, VisibilityTimeout = 0 })
            .ToList();
        var resp = await sqs.ChangeMessageVisibilityBatchAsync(new ChangeMessageVisibilityBatchRequest { QueueUrl = url, Entries = cmvEntries });
        Ensure(resp.Failed is null or { Count: 0 }, $"ChangeMessageVisibilityBatch failures: [{string.Join(", ", (resp.Failed ?? []).Select(f => $"{f.Id}:{f.Code}"))}]");
        var again = new List<Message>();
        seen = [];
        idle = 0;
        for (var attempt = 0; attempt < 15; attempt++)
        {
            var r = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 1 });
            var msgs = r.Messages ?? [];
            if (msgs.Count == 0)
            {
                idle++;
                if (again.Count > 0 && idle >= 3)
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
                    again.Add(m);
                }

                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle });
            }

            if (again.Count >= 2)
            {
                break;
            }
        }

        Ensure(again.Count == 2, $"expected 2 re-exposed messages, got {again.Count}");
    }
}

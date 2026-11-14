// Translated from behavioral/sqs/check_batch_send_receive_count.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.batch-send-receive-count</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchSendReceiveCount
{
    private static readonly Dictionary<string, string> Tags = new() { ["sim2real:managed"] = "true", ["sim2real"] = "batch-send-receive-count" };

    /// <summary>
    /// SendMessageBatch(3) → all three distinct bodies are receivable.
    /// </summary>
    [Test]
    [DisplayName("sqs.batch-send-receive-count")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-"), Tags = new(Tags) })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var token = Guid.NewGuid().ToString("N")[..8];
        var entries = Enumerable.Range(1, 3)
            .Select(i => new SendMessageBatchRequestEntry { Id = i.ToString(CultureInfo.InvariantCulture), MessageBody = $"batch-{i}-{token}" })
            .ToList();
        var resp = await sqs.SendMessageBatchAsync(new SendMessageBatchRequest { QueueUrl = url, Entries = entries });
        Ensure(resp.Failed is null or { Count: 0 }, $"SendMessageBatch failures: [{string.Join(", ", (resp.Failed ?? []).Select(f => f.Id))}]");
        var expected = entries.Select(e => e.MessageBody).ToHashSet();
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

                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle });
            }

            if (received.Count >= 3)
            {
                break;
            }
        }

        var got = received.Select(m => m.Body).ToHashSet();
        Ensure(got.SetEquals(expected),
            $"expected [{string.Join(", ", expected.Order(StringComparer.Ordinal))}], got [{string.Join(", ", got.Order(StringComparer.Ordinal))}]");
    }
}

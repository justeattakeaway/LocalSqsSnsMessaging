// Translated from behavioral/sqs/check_fifo_ordering.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.fifo-ordering</summary>
[Category("AwsEvaluationSuite")]
public sealed class FifoOrdering
{
    /// <summary>
    /// FIFO messages in one group are received in send order.
    /// </summary>
    [Test]
    [DisplayName("sqs.fifo-ordering")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-") + ".fifo",
            Attributes = new() { ["FifoQueue"] = "true" },
            Tags = new() { ["sim2real:managed"] = "true", ["sim2real"] = "fifo-ordering" },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var token = Guid.NewGuid().ToString("N")[..8];
        const string group = "sim2real-group";
        var bodies = Enumerable.Range(0, 3).Select(i => $"fifo-order-{i}-{token}").ToList();
        for (var i = 0; i < bodies.Count; i++)
        {
            await sqs.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = url,
                MessageBody = bodies[i],
                MessageGroupId = group,
                MessageDeduplicationId = $"{token}-{i}",
            });
        }

        List<Message> received = [];
        HashSet<string> seen = [];
        var idle = 0;
        for (var r = 0; r < 15; r++)
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

            if (received.Count >= 3)
            {
                break;
            }
        }

        var got = received.Select(m => m.Body).ToList();
        Ensure(got.SequenceEqual(bodies),
            $"FIFO order mismatch: sent [{string.Join(", ", bodies)}], received [{string.Join(", ", got)}]");
    }
}

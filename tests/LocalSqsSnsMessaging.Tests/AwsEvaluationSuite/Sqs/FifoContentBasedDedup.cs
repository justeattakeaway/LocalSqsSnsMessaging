// Translated from behavioral/sqs/check_fifo_content_based_dedup.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.fifo-content-based-dedup</summary>
[Category("AwsEvaluationSuite")]
public sealed class FifoContentBasedDedup
{
    private static readonly Dictionary<string, string> Tags = new() { ["sim2real:managed"] = "true", ["sim2real"] = "fifo-content-based-dedup" };

    /// <summary>
    /// ContentBasedDeduplication: two sends of the same body (no dedup id) → ONE message.
    /// </summary>
    [Test]
    [DisplayName("sqs.fifo-content-based-dedup")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-") + ".fifo",
            Attributes = new() { ["FifoQueue"] = "true", ["ContentBasedDeduplication"] = "true" },
            Tags = new(Tags),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var token = Guid.NewGuid().ToString("N")[..8];
        var body = $"cbd-{token}";
        const string group = "sim2real-group";
        var sends = new List<SendMessageResponse>();
        for (var i = 0; i < 2; i++)
        {
            sends.Add(await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = body, MessageGroupId = group }));
        }

        if (sends[1].MessageId != sends[0].MessageId)
        {
            throw Fail(
                "content-based dedup broken: identical resend minted a new MessageId " +
                $"{sends[1].MessageId} instead of echoing {sends[0].MessageId}");
        }

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

            if (received.Count >= 2)
            {
                break;
            }
        }

        if (received.Count != 1)
        {
            throw Fail($"expected 1 message after content-based dedup, got {received.Count}");
        }

        if (received[0].MessageId != sends[0].MessageId)
        {
            throw Fail(
                $"delivered MessageId {received[0].MessageId} != the retained send's " +
                $"{sends[0].MessageId}");
        }
    }
}

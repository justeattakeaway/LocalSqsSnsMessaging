// Translated from behavioral/sqs/check_message_timer.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.message-timer</summary>
[Category("AwsEvaluationSuite")]
public sealed class MessageTimer
{
    private const int DelaySeconds = 2;

    private static Dictionary<string, string> Tags => new() { ["sim2real:managed"] = "true", ["sim2real"] = "message-timer" };

    /// <summary>
    /// Per-message DelaySeconds=2: immediate receive empty, arrives after delay.
    /// </summary>
    [Test]
    [DisplayName("sqs.message-timer")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-"), Tags = Tags })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var body = $"msg-timer-{Guid.NewGuid().ToString("N")[..8]}";
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = body, DelaySeconds = DelaySeconds });
        // Immediate receive should be empty: the delay has not elapsed yet.
        var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 0 });
        var messages = resp.Messages ?? [];
        if (messages.Count > 0)
        {
            throw Fail("expected no messages immediately with per-message delay");
        }

        // Bounded retry until the delay has elapsed — no unconditional sleep; each poll's own
        // long-poll wait is itself bounded, and the deadline covers >=3x the declared delay.
        var window = Math.Max(3 * DelaySeconds, 10);
        var deadline = FidelityTime.StartNew();
        var received = new List<Message>();
        var seen = new HashSet<string>();
        while (deadline.Elapsed < TimeSpan.FromSeconds(window) && received.Count == 0)
        {
            var r = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 2 });
            foreach (var m in r.Messages ?? [])
            {
                if (seen.Add(m.MessageId))
                {
                    received.Add(m);
                }

                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle });
            }
        }

        if (received.Count == 0)
        {
            throw Fail(
                $"message never arrived after per-message delay ({DelaySeconds}s) expired, " +
                $"within a {window}s bounded retry window");
        }

        if (received[0].Body != body)
        {
            throw Fail($"body mismatch: '{received[0].Body}' != '{body}'");
        }
    }
}

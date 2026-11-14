// Translated from behavioral/sqs/check_delete_then_empty.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.delete-then-empty</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteThenEmpty
{
    private static readonly Dictionary<string, string> Tags = new() { ["sim2real:managed"] = "true", ["sim2real"] = "delete-then-empty" };

    // Deliberately short so the check's own runtime, not the queue's 30s default, bounds how
    // long we have to wait for the twin control to prove the wiring below.
    private const int VisibilityTimeoutSeconds = 3;
    private const string DeleteBody = "delete-then-empty:delete-me";
    private const string KeepBody = "delete-then-empty:keep-me";

    /// <summary>
    /// After DeleteMessage the message never reappears, even past its visibility timeout, while an
    /// undeleted sibling received with it reappears on schedule as the control.
    /// </summary>
    [Test]
    [DisplayName("sqs.delete-then-empty")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-"), Tags = new(Tags) })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = DeleteBody });
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = KeepBody });

        var received = new Dictionary<string, Message>();
        var deadline = Stopwatch.StartNew();
        while (received.Count < 2 && deadline.Elapsed.TotalSeconds < 30.0)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 2,
                VisibilityTimeout = VisibilityTimeoutSeconds,
            });
            foreach (var msg in resp.Messages ?? [])
            {
                received.TryAdd(msg.Body, msg);
            }
        }

        if (received.Count < 2)
        {
            throw Fail($"expected both messages receivable, got [{string.Join(", ", received.Keys.Order(StringComparer.Ordinal))}]");
        }

        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = received[DeleteBody].ReceiptHandle });

        // Hold the window well past the visibility timeout: long enough for the undeleted
        // sibling to redeliver with margin, short enough to keep the check fast.
        var seen = new HashSet<string>();
        var windowDeadline = Stopwatch.StartNew();
        while (!seen.Contains(KeepBody) && windowDeadline.Elapsed.TotalSeconds < VisibilityTimeoutSeconds * 5)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 });
            foreach (var msg in resp.Messages ?? [])
            {
                seen.Add(msg.Body);
            }

            Ensure(!seen.Contains(DeleteBody), "deleted message reappeared after its visibility timeout expired");
        }

        Ensure(seen.Contains(KeepBody),
            "positive control failed: the undeleted sibling never reappeared after its visibility " +
            "timeout elapsed, so the absence of the deleted message is inconclusive");
    }
}

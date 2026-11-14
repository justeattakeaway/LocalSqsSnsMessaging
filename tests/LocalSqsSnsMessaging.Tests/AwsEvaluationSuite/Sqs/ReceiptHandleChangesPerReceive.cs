// Translated from behavioral/sqs/check_receipt_handle_changes_per_receive.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.receipt-handle-changes-per-receive</summary>
[Category("AwsEvaluationSuite")]
public sealed class ReceiptHandleChangesPerReceive
{
    private const string Check = "receipt-handle-changes-per-receive";

    /// <summary>
    /// After a message's visibility timeout expires and it becomes receivable again, the
    /// redelivery carries a distinct receipt handle -- SQS never reuses a receipt handle across
    /// separate receives of the same message.
    /// </summary>
    [Test]
    [DisplayName("sqs.receipt-handle-changes-per-receive")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-rh-"),
            Attributes = new() { ["VisibilityTimeout"] = "2" },
            Tags = new() { ["sim2real:managed"] = "true", ["sim2real"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "receipt-handle-test" });

        var first = await PollForMessage(ctx, url, timeoutS: 30.0);
        var firstHandle = first.ReceiptHandle;
        var firstId = first.MessageId;

        // Do not delete -- let the 2s visibility timeout expire and wait for redelivery.
        var second = await PollForRedelivery(ctx, url, expectedMessageId: firstId, timeoutS: 30.0);
        var secondHandle = second.ReceiptHandle;

        Ensure(secondHandle != firstHandle,
            $"receipt handle was reused across separate receives: first='{firstHandle}' "
            + $"second='{secondHandle}'");
        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = secondHandle });

        var remainder = await Drain(ctx, url, timeoutS: 8.0);
        Ensure(remainder.Count == 0,
            $"message '{firstId}' still deliverable after delete_message with the "
            + $"current receipt handle: got {remainder.Count} message(s)");
    }

    private static async Task<Message> PollForMessage(FidelityContext ctx, string url, double timeoutS = 30.0)
    {
        var sqs = ctx.Sqs;
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 5 });
            var msgs = resp.Messages ?? [];
            if (msgs.Count > 0)
            {
                return msgs[0];
            }
        }

        throw Fail("no message received within timeout");
    }

    private static async Task<Message> PollForRedelivery(FidelityContext ctx, string url, string expectedMessageId, double timeoutS = 30.0)
    {
        var sqs = ctx.Sqs;
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 5 });
            foreach (var msg in resp.Messages ?? [])
            {
                if (msg.MessageId == expectedMessageId)
                {
                    return msg;
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(0.5));
        }

        throw Fail("message was never redelivered after visibility timeout expiry");
    }

    /// <summary>
    /// Poll for the full window and return every message seen -- used to prove absence
    /// (a deleted message must not come back), never to prove presence.
    /// </summary>
    private static async Task<List<Message>> Drain(FidelityContext ctx, string url, double timeoutS)
    {
        var sqs = ctx.Sqs;
        var deadline = Stopwatch.StartNew();
        List<Message> seen = [];
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
            seen.AddRange(resp.Messages ?? []);
        }

        return seen;
    }
}

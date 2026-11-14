// Translated from behavioral/sqs/check_change_message_visibility_expired_handle_accepted.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using System.Globalization;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.change-message-visibility-expired-handle-accepted</summary>
[Category("AwsEvaluationSuite")]
public sealed class ChangeMessageVisibilityExpiredHandleAccepted
{
    /// <summary>
    /// A receipt handle whose visibility window lapsed stays valid: ChangeMessageVisibility with it
    /// re-hides the message rather than failing with MessageNotInflight, and it can release it again.
    /// </summary>
    [Test]
    [DisplayName("sqs.change-message-visibility-expired-handle-accepted")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.ReceiptHandles, "message stayed visible after re-hiding it with the lapsed receipt handle")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-"),
            Attributes = new() { ["VisibilityTimeout"] = "1" },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "expiring" });
        var first = await ReceiveMessages(sqs, url, minCount: 1, maxBatch: 1, attempts: 20);
        Ensure(first.Count > 0, "message never became visible");
        var staleHandle = first[0].ReceiptHandle;

        // observe the 1s visibility window lapse service-natively: the in-flight counter returns
        // to zero without any consuming receive.
        await Poll(
            async () => int.Parse((await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = url, AttributeNames = ["ApproximateNumberOfMessagesNotVisible"],
            })).Attributes!.GetValueOrDefault("ApproximateNumberOfMessagesNotVisible", "0"), CultureInfo.InvariantCulture),
            n => n == 0,
            timeoutS: 90,
            intervalS: 3);

        // the lapsed handle is still accepted and must re-hide the message
        await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest { QueueUrl = url, ReceiptHandle = staleHandle, VisibilityTimeout = 120 });

        var hidden = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 3 })).Messages ?? [];
        Ensure(hidden.Count == 0, "message stayed visible after re-hiding it with the lapsed receipt handle");

        // and the same handle can release it again
        await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest { QueueUrl = url, ReceiptHandle = staleHandle, VisibilityTimeout = 0 });
        var released = await ReceiveMessages(sqs, url, minCount: 1, maxBatch: 1, attempts: 30);
        Ensure(released.Count > 0 && released[0].Body == "expiring", "released message body mismatch");
    }

    private static async Task<T> Poll<T>(Func<Task<T>> fn, Func<T, bool> pred, double timeoutS, double intervalS)
    {
        var deadline = Stopwatch.StartNew();
        var val = await fn();
        while (!pred(val))
        {
            if (deadline.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw Fail($"condition not met within {timeoutS}s (last={val})");
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalS));
            val = await fn();
        }

        return val;
    }

    private static async Task<List<Message>> ReceiveMessages(AmazonSQSClient sqs, string url, int minCount, int maxBatch = 10, int attempts = 15)
    {
        var received = new List<Message>();
        var seen = new HashSet<string>();
        var idle = 0;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var r = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = maxBatch, WaitTimeSeconds = 1 });
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

            if (received.Count >= minCount)
            {
                break;
            }
        }

        return received;
    }
}

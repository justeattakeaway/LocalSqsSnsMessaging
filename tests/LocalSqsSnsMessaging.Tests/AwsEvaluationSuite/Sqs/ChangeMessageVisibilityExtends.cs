// Translated from behavioral/sqs/check_change_message_visibility_extends.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using System.Globalization;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.change-message-visibility-extends</summary>
[Category("AwsEvaluationSuite")]
public sealed class ChangeMessageVisibilityExtends
{
    /// <summary>
    /// ChangeMessageVisibility extends invisibility past the original 2s VisibilityTimeout, but
    /// boundedly: the message stays hidden past 2s and reappears once the extended 10s deadline passes.
    /// </summary>
    // timing:real-time — visibility: 10 s https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/sqs-visibility-timeout.html
    [Test]
    [DisplayName("sqs.change-message-visibility-extends")]
    [Timeout(FidelityContext.RealTimeBudgetMilliseconds)]
    [Category(ApplyDefaultCategoryAttribute.TimeBasedCategoryName)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.RealTimeBudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        const int originalVt = 2;
        const int extendedVt = 10;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-"),
            Attributes = new() { ["VisibilityTimeout"] = originalVt.ToString(CultureInfo.InvariantCulture) },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "vis-extend-test" });
        var msg = await PollReceiveOne(sqs, url, timeoutS: 15.0);

        var tExtend = Stopwatch.StartNew();
        await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest
        {
            QueueUrl = url, ReceiptHandle = msg.ReceiptHandle, VisibilityTimeout = extendedVt,
        });

        // Past the original VT but well inside the extended VT, the message must stay hidden.
        var holdDeadline = TimeSpan.FromSeconds(extendedVt - 3);
        var checkedPastOriginalVt = false;
        while (tExtend.Elapsed < holdDeadline)
        {
            var elapsed = tExtend.Elapsed.TotalSeconds;
            if (elapsed >= originalVt + 0.5)
            {
                checkedPastOriginalVt = true;
                var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 0 });
                var visible = resp.Messages ?? [];
                Ensure(visible.Count == 0,
                    $"message visible at {elapsed:F1}s despite ChangeMessageVisibility " +
                    $"extending to {extendedVt}s (original VT was {originalVt}s): [{string.Join(", ", visible.Select(m => m.MessageId))}]");
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        Ensure(checkedPastOriginalVt, "hold window ended before a read landed past the original VT -- widen it");

        // The extension must be bounded: the same message must reappear after the extended deadline,
        // which catches a target that hides it forever.
        var reappeared = await PollReceiveOne(sqs, url, timeoutS: extendedVt + 15.0, expectedMessageId: msg.MessageId);
        var reappearedAt = tExtend.Elapsed.TotalSeconds;
        Ensure(reappearedAt >= extendedVt - 1,
            $"message reappeared at {reappearedAt:F1}s, before the extended VT of " +
            $"{extendedVt}s had elapsed -- extension did not hold for its full duration");
        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = reappeared.ReceiptHandle });
    }

    private static async Task<Message> PollReceiveOne(AmazonSQSClient sqs, string url, double timeoutS, string? expectedMessageId = null)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 2 });
            foreach (var m in resp.Messages ?? [])
            {
                if (expectedMessageId is null || m.MessageId == expectedMessageId)
                {
                    return m;
                }
            }
        }

        throw Fail($"no matching message received within {timeoutS:F0}s");
    }
}

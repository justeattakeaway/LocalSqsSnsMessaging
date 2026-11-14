// Translated from behavioral/sqs/check_change_message_visibility_extends_timeout.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.change-message-visibility-extends-timeout</summary>
[Category("AwsEvaluationSuite")]
public sealed class ChangeMessageVisibilityExtendsTimeout
{
    /// <summary>
    /// ChangeMessageVisibility extends the hidden window past the queue's original
    /// VisibilityTimeout; the message stays hidden through the original deadline and only
    /// reappears once the *extended* deadline elapses.
    /// </summary>
    // timing:real-time — visibility: 12 s https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/sqs-visibility-timeout.html
    [Test]
    [DisplayName("sqs.change-message-visibility-extends-timeout")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [Category(ApplyDefaultCategoryAttribute.TimeBasedCategoryName)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.RealTimeBudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-sqs-cvt-"),
            Attributes = new() { ["VisibilityTimeout"] = "3" },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        const string body = "cvt-extend-message";
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = body });
        var msg = await PollReceiveOne(sqs, url, timeoutS: 15.0);
        Ensure(msg.Body == body, "assert msg[\"Body\"] == body");

        var tChange = FidelityTime.StartNew();
        await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest
        {
            QueueUrl = url, ReceiptHandle = msg.ReceiptHandle, VisibilityTimeout = 12,
        });

        // Hold well past the queue's *original* 3s timeout, polling receive throughout
        // (bounded retry, not a single sample after a blind sleep): every sample in the
        // hold window must find the message still hidden, which is what proves the
        // extension actually moved the deadline rather than merely delaying one check.
        var holdDeadline = TimeSpan.FromSeconds(9.0);
        while (tChange.Elapsed < holdDeadline)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 1 });
            Ensure(resp.Messages is null or { Count: 0 },
                $"message reappeared {tChange.Elapsed.TotalSeconds:F1}s after " +
                "ChangeMessageVisibility, before the extended 12s window elapsed");
        }

        // Poll (bounded) for it to reappear once the extended window elapses, and assert
        // the reappearance tracks the *extended* deadline, not the original 3s one.
        var second = await PollReceiveOne(sqs, url, timeoutS: 20.0);
        var reappearedAt = tChange.Elapsed.TotalSeconds;
        Ensure(second.Body == body, "assert second[\"Body\"] == body");
        Ensure(reappearedAt >= 9.0,
            $"message reappeared only {reappearedAt:F1}s after ChangeMessageVisibility, " +
            "inside the window we already held open above -- the extended visibility " +
            "did not hold");
        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = second.ReceiptHandle });
    }

    private static async Task<Message> PollReceiveOne(AmazonSQSClient sqs, string url, double timeoutS = 15.0)
    {
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 2 });
            if (resp.Messages is { Count: > 0 } messages)
            {
                return messages[0];
            }
        }

        throw Fail($"no message received within {timeoutS}s");
    }
}

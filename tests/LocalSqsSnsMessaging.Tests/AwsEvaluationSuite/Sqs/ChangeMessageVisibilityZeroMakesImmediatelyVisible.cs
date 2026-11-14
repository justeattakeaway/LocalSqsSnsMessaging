// Translated from behavioral/sqs/check_change_message_visibility_zero_makes_immediately_visible.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.change-message-visibility-zero-makes-immediately-visible</summary>
[Category("AwsEvaluationSuite")]
public sealed class ChangeMessageVisibilityZeroMakesImmediatelyVisible
{
    /// <summary>
    /// ChangeMessageVisibility with VisibilityTimeout=0 makes the message receivable again well before
    /// its long original timeout; a control leg first proves that long timeout is in effect.
    /// </summary>
    [Test]
    [DisplayName("sqs.change-message-visibility-zero-makes-immediately-visible")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-sqs-cvz-"),
            Attributes = new() { ["VisibilityTimeout"] = "120" },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        const string body = "cvz-immediate-message";
        var t0 = FidelityTime.StartNew();
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = body });
        var msg = await PollReceiveOne(sqs, url, timeoutS: 15.0);
        var firstReceiveLatencyS = t0.Elapsed.TotalSeconds;
        Ensure(msg!.Body == body, $"expected body '{body}', got '{msg.Body}'");

        // Control: the 120s queue timeout must still hide the message after >=3x its first delivery
        // latency, or the VT=0 leg cannot tell a real override from no enforcement at all.
        var holdWindowS = Math.Max(3.0 * firstReceiveLatencyS, 5.0);
        var stillHidden = await PollReceiveOne(sqs, url, timeoutS: holdWindowS, expectNone: true);
        Ensure(stillHidden is null,
            "expected message to remain hidden under the 120s VisibilityTimeout for " +
            $"{holdWindowS:F1}s, but it reappeared: {stillHidden?.MessageId}");

        await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle, VisibilityTimeout = 0 });

        // Well under the 120s original timeout, the message must already be receivable again.
        var second = await PollReceiveOne(sqs, url, timeoutS: 10.0);
        Ensure(second!.Body == body, $"expected body '{body}' after VT=0, got '{second.Body}'");
        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = second.ReceiptHandle });
    }

    private static async Task<Message?> PollReceiveOne(AmazonSQSClient sqs, string url, double timeoutS = 15.0, bool expectNone = false)
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

        if (expectNone)
        {
            return null;
        }

        throw Fail($"no message received within {timeoutS}s");
    }
}

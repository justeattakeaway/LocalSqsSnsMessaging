// Translated from behavioral/sqs/check_delete_message_stale_receipt_handle_after_redelivery_succeeds.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using System.Net;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.delete-message-stale-receipt-handle-after-redelivery-succeeds</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteMessageStaleReceiptHandleAfterRedeliverySucceeds
{
    private const string Check = "delete-message-stale-receipt-handle-after-redelivery-succeeds";

    /// <summary>
    /// A redelivered message carries a new receipt handle, and DeleteMessage with the handle from
    /// its first receive still succeeds rather than being refused as invalid.
    ///
    /// Reference: https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_DeleteMessage.html
    /// </summary>
    [Test]
    [DisplayName("sqs.delete-message-stale-receipt-handle-after-redelivery-succeeds")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-stale-"),
            Attributes = new() { ["VisibilityTimeout"] = "2" },
            Tags = new() { ["sim2real"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var sentId = (await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "stale-handle-probe" })).MessageId;
        var first = await PollForMessage(sqs, url, sentId, timeoutSeconds: BudgetSeconds(ctx, 30.0));
        var firstHandle = first.ReceiptHandle;

        // The 2 s visibility timeout lapses, so the same message is redelivered under a new handle.
        var second = await PollForMessage(sqs, url, sentId, timeoutSeconds: BudgetSeconds(ctx, 30.0));
        var secondHandle = second.ReceiptHandle;
        Ensure(secondHandle != firstHandle,
            $"the redelivery of {sentId} reused its first receipt handle {firstHandle}");

        // AWS documents only that the call succeeds; whether it deletes the message is not promised.
        var resp = await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = firstHandle });
        var status = resp.HttpStatusCode;
        Ensure(status == HttpStatusCode.OK,
            "DeleteMessage with the first-receive handle of a redelivered message answered HTTP " +
            $"{(int)status}, expected 200");
    }

    /// <summary>Receive until the message `message_id` is delivered; return it.</summary>
    private static async Task<Message> PollForMessage(AmazonSQSClient sqs, string url, string messageId, double timeoutSeconds)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed.TotalSeconds < timeoutSeconds)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 2 });
            foreach (var msg in resp.Messages ?? [])
            {
                if (msg.MessageId == messageId)
                {
                    return msg;
                }
            }
        }

        throw new TimeoutException($"message {messageId} was not delivered within {timeoutSeconds:F0}s");
    }

    /// <summary>A wait's timeout, bounded by the runner's remaining time so the wait fails with its own message.</summary>
    private static double BudgetSeconds(FidelityContext ctx, double capSeconds)
    {
        var left = ctx.TimeLeftSeconds;
        return Math.Max(1.0, Math.Min(capSeconds, left - 5.0));
    }
}

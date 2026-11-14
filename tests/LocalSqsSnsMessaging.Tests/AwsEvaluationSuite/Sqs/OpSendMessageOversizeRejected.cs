// Translated from behavioral/sqs/check_op_send_message_oversize_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.op-send-message-oversize-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class OpSendMessageOversizeRejected
{
    private const string Check = "op-send-message-oversize-rejected";

    // Exact messages real SQS returns for these refusals.
    private const string MsgOversize = "One or more parameters are invalid. Reason: Message must be shorter than 1024 bytes.";

    /// <summary>Call fn(); return its botocore Error dict. An unexpected success raises.</summary>
    private static async Task<AwsError> ClientError(Func<Task<SendMessageResponse>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error ?? throw Fail($"expected rejection but call succeeded and returned {{MD5OfMessageBody: {result!.MD5OfMessageBody}, MessageId: {result.MessageId}}}");
    }

    /// <summary>
    /// SendMessage rejects a body larger than MaximumMessageSize rather than truncating it; an
    /// in-bounds body on the same queue enqueues and reads back first as a positive control.
    /// </summary>
    [Test]
    [DisplayName("sqs.op-send-message-oversize-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-"),
            Attributes = new() { ["MaximumMessageSize"] = "1024" },
            Tags = new() { ["sim2real"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        // --- positive control: an in-bounds body on this exact queue enqueues and reads back ---
        var controlBody = new string('c', 100);
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = controlBody });
        var controlMsgs = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 })).Messages ?? [];
        Ensure(controlMsgs.Count == 1 && controlMsgs[0].Body == controlBody,
            $"positive control failed: expected one message with body '{controlBody}', " +
            $"got [{string.Join(", ", controlMsgs.Select(m => m.Body))}]");
        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = controlMsgs[0].ReceiptHandle });

        // --- break: a body over MaximumMessageSize on the SAME queue is refused, not truncated ---
        var err = await ClientError(() => sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = new string('a', 2048) }));
        Ensure(err.Code == "InvalidParameterValue", $"got {err}");
        Ensure(err.Message == MsgOversize, $"got {err}");

        var msgs = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 })).Messages ?? [];
        Ensure(msgs.Count == 0, $"oversized body was accepted anyway: [{string.Join(", ", msgs.Select(m => m.MessageId))}]");
    }
}

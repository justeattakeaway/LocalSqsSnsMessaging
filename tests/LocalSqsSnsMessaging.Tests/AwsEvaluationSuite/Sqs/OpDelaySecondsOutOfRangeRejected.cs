// Translated from behavioral/sqs/check_op_delay_seconds_out_of_range_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.op-delay-seconds-out-of-range-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class OpDelaySecondsOutOfRangeRejected
{
    private const string DelayOutOfRangeMessage =
        "Value 901 for parameter DelaySeconds is invalid. Reason: DelaySeconds must be >= 0 and <= 900.";

    /// <summary>
    /// Call fn(); return its botocore Error dict. An unexpected SUCCESS raises, because an emulator
    /// ACCEPTING input real AWS refuses is the strongest signal available.
    /// </summary>
    private static async Task<AwsError> ClientError(Func<Task<SendMessageResponse>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error ?? throw Fail($"expected DelaySeconds=901 to be rejected, but it SUCCEEDED: {{MD5OfMessageBody: {result!.MD5OfMessageBody}, MessageId: {result.MessageId}}}");
    }

    /// <summary>
    /// SendMessage DelaySeconds above the 900s maximum must be rejected, not clamped.
    /// </summary>
    [Test]
    [DisplayName("sqs.op-delay-seconds-out-of-range-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        // positive control: the boundary value itself (900s, the documented maximum) must be
        // accepted, so a rejection below can only mean "out of range", not "every DelaySeconds fails"
        var (okResp, okErr) = await FidelityContext.Capture(() => sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "boundary-ok", DelaySeconds = 900 }));
        if (okErr is not null)
        {
            throw new FidelityAssertionException($"DelaySeconds=900 (the documented maximum) must be accepted, got {okErr.Code}", okErr.Exception);
        }

        var okId = okResp!.MessageId ?? throw new KeyNotFoundException("MessageId");
        Ensure(!string.IsNullOrEmpty(okId), "SendMessage at the boundary did not return a MessageId");

        var error = await ClientError(() => sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "x", DelaySeconds = 901 }));
        Ensure(error.Code == "InvalidParameterValue", $"got {error.Code}");
        Ensure(error.Message == DelayOutOfRangeMessage, $"expected message '{DelayOutOfRangeMessage}', got '{error.Message}'");

        // the rejection must not have enqueued anything (the boundary-ok message is still delayed
        // 900s, so it cannot appear here either -- this reads only what DelaySeconds=901 caused)
        var msgs = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 })).Messages ?? [];
        Ensure(msgs.Count == 0, $"rejected send still enqueued a message: [{string.Join(", ", msgs.Select(m => m.Body))}]");
    }
}

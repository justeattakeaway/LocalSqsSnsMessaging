// Translated from behavioral/sqs/check_op_receive_message_param_bounds_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.op-receive-message-param-bounds-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class OpReceiveMessageParamBoundsRejected
{
    // Exact messages real SQS returns for these refusals.
    private const string MsgMax = "Value 11 for parameter MaxNumberOfMessages is invalid. Reason: Must be between 1 and 10, if provided.";
    private const string MsgWait = "Value 21 for parameter WaitTimeSeconds is invalid. Reason: Must be >= 0 and <= 20, if provided.";

    /// <summary>Call fn(); return its botocore Error dict. An unexpected SUCCESS raises.</summary>
    private static async Task<AwsError> ClientError(Func<Task<ReceiveMessageResponse>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error ?? throw Fail($"expected rejection but the call succeeded: {{HTTPStatusCode: {result!.HttpStatusCode}, Messages: {result.Messages?.Count}}}");
    }

    /// <summary>
    /// ReceiveMessage must reject MaxNumberOfMessages > 10 and WaitTimeSeconds > 20, while a
    /// well-formed call on the same queue keeps working before and after.
    /// </summary>
    [Test]
    [DisplayName("sqs.op-receive-message-param-bounds-rejected")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-"),
            Tags = new() { ["sim2real:managed"] = "true", ["sim2real:prefix"] = "sim2real-behav" },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        async Task WellFormedReceiveWorks()
        {
            await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "bounds-control" });
            var msgs = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 })).Messages ?? [];
            Ensure(msgs.Count > 0, "well-formed ReceiveMessage returned no messages");
        }

        // positive control: this queue and this API accept a well-formed call before we test the bound
        await WellFormedReceiveWorks();

        var errMax = await ClientError(() => sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 11 }));
        Ensure(errMax.Code == "InvalidParameterValue", $"got {errMax}");
        Ensure(errMax.Message == MsgMax, $"got {errMax}");

        var errWait = await ClientError(() => sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, WaitTimeSeconds = 21 }));
        Ensure(errWait.Code == "InvalidParameterValue", $"got {errWait}");
        Ensure(errWait.Message == MsgWait, $"got {errWait}");

        // the rejections are specific to the out-of-bounds input, not a queue left broken by them
        await WellFormedReceiveWorks();
    }
}

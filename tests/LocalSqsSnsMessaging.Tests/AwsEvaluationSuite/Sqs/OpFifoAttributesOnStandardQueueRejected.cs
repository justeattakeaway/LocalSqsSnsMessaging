// Translated from behavioral/sqs/check_op_fifo_attributes_on_standard_queue_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.op-fifo-attributes-on-standard-queue-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class OpFifoAttributesOnStandardQueueRejected
{
    // Exact messages real SQS returns for these refusals.
    private const string MsgCbd = "Unknown Attribute ContentBasedDeduplication.";
    private const string MsgFifo = "Unknown Attribute FifoQueue.";

    /// <summary>Call fn(); return its botocore Error dict. An unexpected SUCCESS raises.</summary>
    private static async Task<AwsError> ClientError<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error ?? throw Fail($"expected rejection but call succeeded and returned {{HTTPStatusCode: {(result as Amazon.Runtime.AmazonWebServiceResponse)?.HttpStatusCode}}}");
    }

    /// <summary>
    /// FIFO-only queue attributes must be rejected on a standard queue, and FifoQueue is immutable.
    /// </summary>
    [Test]
    [DisplayName("sqs.op-fifo-attributes-on-standard-queue-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var errCbd = await ClientError(() => sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = url,
            Attributes = new() { ["ContentBasedDeduplication"] = "true" },
        }));
        Ensure(errCbd.Code == "InvalidAttributeName", $"got {errCbd}");
        Ensure(errCbd.Message == MsgCbd, $"got {errCbd}");

        var errFifo = await ClientError(() => sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = url,
            Attributes = new() { ["FifoQueue"] = "true" },
        }));
        Ensure(errFifo.Code == "InvalidAttributeName", $"got {errFifo}");
        Ensure(errFifo.Message == MsgFifo, $"got {errFifo}");

        var attrs = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["All"] })).Attributes!;
        Ensure(!attrs.ContainsKey("FifoQueue"), "standard queue was mutated into a FIFO queue");

        // Positive control: a genuinely writable attribute still succeeds on this same queue,
        // proving the two rejections above are specific to FIFO-only attributes and not a sign
        // that SetQueueAttributes is broken or denied outright for this queue.
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = url, Attributes = new() { ["VisibilityTimeout"] = "45" } });
        var control = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["VisibilityTimeout"] })).Attributes!["VisibilityTimeout"];
        Ensure(control == "45", $"positive control failed: got {control}");
    }
}

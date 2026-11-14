// Translated from behavioral/sqs/check_op_queue_name_validation_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.op-queue-name-validation-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class OpQueueNameValidationRejected
{
    // Exact messages real SQS returns for these refusals.
    private const string MsgName = "Can only include alphanumeric characters, hyphens, or underscores. 1 to 80 in length";
    private const string MsgSuffix = "The name of a FIFO queue can only include alphanumeric characters, hyphens, or underscores, must end with .fifo suffix and be 1 to 80 in length.";

    /// <summary>Call fn(); return its botocore Error dict. An unexpected SUCCESS raises.</summary>
    private static async Task<AwsError> ClientError(Func<Task<CreateQueueResponse>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error ?? throw Fail($"expected rejection but call succeeded and returned {{QueueUrl: {result!.QueueUrl}}}");
    }

    /// <summary>
    /// CreateQueue must reject illegal queue names and FIFO/suffix mismatches, but accept a valid one.
    /// </summary>
    [Test]
    [DisplayName("sqs.op-queue-name-validation-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;

        var okName = FidelityContext.UniqueName("sim2real-behav-name-ok-");
        var okUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = okName, Tags = new() { ["sim2real-audit"] = "true" } })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(okUrl));
        var lookedUp = (await sqs.GetQueueUrlAsync(new GetQueueUrlRequest { QueueName = okName })).QueueUrl;
        Ensure(lookedUp == okUrl, $"positive control: valid queue name not visible via GetQueueUrl: {lookedUp}");

        var badCharName = FidelityContext.UniqueName("sim2real-behav-bad!name-");
        var errChars = await ClientError(() => sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = badCharName }));
        Ensure(errChars.Code == "InvalidParameterValue", $"got {errChars}");
        Ensure(errChars.Message == MsgName, $"got {errChars}");

        var fifoNoSuffix = FidelityContext.UniqueName("sim2real-behav-nofifo-");
        var errSuffix = await ClientError(() => sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = fifoNoSuffix,
            Attributes = new() { ["FifoQueue"] = "true" },
        }));
        Ensure(errSuffix.Code == "InvalidParameterValue", $"got {errSuffix}");
        Ensure(errSuffix.Message == MsgSuffix, $"got {errSuffix}");

        var tooLong = "sim2real-behav-" + new string('z', 100);
        var errLong = await ClientError(() => sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = tooLong }));
        Ensure(errLong.Code == "InvalidParameterValue", $"got {errLong}");
        Ensure(errLong.Message == MsgName, $"got {errLong}");
    }
}

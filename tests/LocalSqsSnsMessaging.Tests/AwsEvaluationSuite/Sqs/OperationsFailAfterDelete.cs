// Translated from behavioral/sqs/check_operations_fail_after_delete.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.operations-fail-after-delete</summary>
[Category("AwsEvaluationSuite")]
public sealed class OperationsFailAfterDelete
{
    private const string QueueNotFound = "AWS.SimpleQueueService.NonExistentQueue";

    // Exact messages real SQS returns for these refusals.
    private const string MsgNotFound = "The specified queue does not exist.";

    private static Dictionary<string, string> Tags => new()
    {
        ["sim2real:managed"] = "true",
        ["sim2real"] = "operations-fail-after-delete",
    };

    /// <summary>Call fn(); return its botocore Error dict. An unexpected SUCCESS raises.</summary>
    private static async Task<AwsError> ClientError<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error ?? throw Fail($"expected rejection but call succeeded and returned {result}");
    }

    private static async Task SafeDelete(AmazonSQSClient sqs, string url)
    {
        var error = await FidelityContext.CaptureError(() => sqs.DeleteQueueAsync(new DeleteQueueRequest { QueueUrl = url }));
        if (error is not null && error.Code is not ("AWS.SimpleQueueService.NonExistentQueue" or "QueueDoesNotExist"))
        {
            throw error.Exception;
        }
    }

    /// <summary>
    /// Send until the deleted queue refuses the call; return that Error dict. Sends that still
    /// succeed inside the propagation window are retried; the deadline raises.
    /// </summary>
    private static async Task<AwsError> SendUntilRejected(AmazonSQSClient sqs, string url, double timeoutSeconds, double intervalSeconds)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            var err = await FidelityContext.CaptureError(() => sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "x" }));
            if (err is not null)
            {
                if (err.Code == QueueNotFound)
                {
                    return err;
                }

                throw new FidelityAssertionException($"expected {QueueNotFound}, got {err}", err.Exception);
            }

            if (deadline.Elapsed >= TimeSpan.FromSeconds(timeoutSeconds))
            {
                throw Fail($"SendMessage still succeeded {timeoutSeconds}s after DeleteQueue");
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }
    }

    /// <summary>
    /// After DeleteQueue, subsequent data-plane and control-plane calls fail with QueueDoesNotExist.
    /// </summary>
    [Test]
    [DisplayName("sqs.operations-fail-after-delete")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.QueryErrorCodes, "expected AWS.SimpleQueueService.NonExistentQueue, got {'Message': 'Queue not found', 'Code': 'QueueDoesNotExist'}")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-"), Tags = Tags })).QueueUrl;
        ctx.OnCleanup(() => SafeDelete(sqs, url));

        // Positive control: the queue accepts a message while it still exists, so a target that
        // rejects every send_message call cannot pass merely by always answering QueueDoesNotExist.
        var control = await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "control" });
        Ensure(!string.IsNullOrEmpty(control.MessageId), "positive control send_message returned no MessageId");

        await sqs.DeleteQueueAsync(new DeleteQueueRequest { QueueUrl = url });

        // DeleteQueue propagation is documented to take up to 60s; the poll ceiling covers it.
        var err = await SendUntilRejected(sqs, url, timeoutSeconds: 240, intervalSeconds: 10);
        Ensure(err.Message == MsgNotFound, $"got {err}");

        // Independent read, at the destination, through a different operation than any of the above.
        var errRead = await ClientError(() => sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["All"] }));
        Ensure(errRead.Code == QueueNotFound, $"expected {QueueNotFound}, got {errRead}");
        Ensure(errRead.Message == MsgNotFound, $"got {errRead}");

        var errDelete = await ClientError(() => sqs.DeleteQueueAsync(new DeleteQueueRequest { QueueUrl = url }));
        Ensure(errDelete.Code == QueueNotFound, $"expected {QueueNotFound}, got {errDelete}");
        Ensure(errDelete.Message == MsgNotFound, $"got {errDelete}");
    }
}

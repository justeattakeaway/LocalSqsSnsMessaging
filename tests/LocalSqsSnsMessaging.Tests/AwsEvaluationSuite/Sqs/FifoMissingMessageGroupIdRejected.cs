// Translated from behavioral/sqs/check_fifo_missing_message_group_id_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.fifo-missing-message-group-id-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class FifoMissingMessageGroupIdRejected
{
    private const string MissingGroupIdMessage = "The request must contain the parameter MessageGroupId.";

    /// <summary>
    /// SendMessage to a FIFO queue without the required MessageGroupId is rejected with
    /// MissingParameter, the message never lands on the queue, and a corrected call (same
    /// queue, MessageGroupId supplied) succeeds.
    /// </summary>
    [Test]
    [DisplayName("sqs.fifo-missing-message-group-id-rejected")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.ErrorMessageText, "expected MissingParameter, got 'InvalidOperationException'")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-sqs-fifo-noreq-") + ".fifo";
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = name,
            Attributes = new() { ["FifoQueue"] = "true" },
            Tags = new() { ["sim2real"] = "fifo-missing-message-group-id-rejected" },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var error = await ClientError(() => sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = url, MessageBody = "missing-group-id", MessageDeduplicationId = "d1",
        }));
        Ensure(error.Code == "MissingParameter", $"expected MissingParameter, got '{error.Code}'");
        Ensure(error.Message == MissingGroupIdMessage,
            $"expected message '{MissingGroupIdMessage}', got '{error.Message}'");

        var attrs = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = url, AttributeNames = ["ApproximateNumberOfMessages"],
        })).Attributes!;
        Ensure(attrs["ApproximateNumberOfMessages"] == "0",
            $"expected 0 visible messages after the rejected send, got '{attrs["ApproximateNumberOfMessages"]}'");
        var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 });
        Ensure(resp.Messages is null or { Count: 0 }, "message was enqueued despite the rejected SendMessage call");

        // positive control: the same call with MessageGroupId supplied succeeds end-to-end
        var sendResp = await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = url,
            MessageBody = "has-group-id",
            MessageGroupId = "g1",
            MessageDeduplicationId = "d2",
        });
        Ensure(sendResp.MessageId is not null, $"corrected SendMessage returned no MessageId: {sendResp}");

        var msg = await PollReceiveOne(sqs, url, timeoutS: 15.0);
        Ensure(msg.Body == "has-group-id", $"expected body 'has-group-id', got '{msg.Body}'");
        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle });
    }

    /// <summary>
    /// The botocore Error dict, or a raise. An unexpected SUCCESS raises rather than returning a
    /// falsy sentinel.
    /// </summary>
    private static async Task<AwsError> ClientError<T>(Func<Task<T>> fn)
        where T : class
    {
        var (resp, error) = await FidelityContext.Capture(fn);
        return error ?? throw Fail($"expected the call to be rejected, but it SUCCEEDED and returned {resp}");
    }

    private static async Task<Message> PollReceiveOne(AmazonSQSClient sqs, string url, double timeoutS = 15.0)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 3 });
            if (resp.Messages is { Count: > 0 } messages)
            {
                return messages[0];
            }
        }

        throw Fail($"no message received within {timeoutS}s");
    }
}

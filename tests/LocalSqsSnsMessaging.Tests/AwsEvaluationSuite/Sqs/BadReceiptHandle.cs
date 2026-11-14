// Translated from behavioral/sqs/check_bad_receipt_handle.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.bad-receipt-handle</summary>
[Category("AwsEvaluationSuite")]
public sealed class BadReceiptHandle
{
    private const string ErrBadReceiptHandle = "ReceiptHandleIsInvalid";
    private static readonly Dictionary<string, string> Tags = new() { ["sim2real:managed"] = "true", ["sim2real"] = "bad-receipt-handle" };

    /// <summary>
    /// delete_message with a syntactically bogus handle -> ReceiptHandleIsInvalid, and
    /// the real message's own handle -- proven live by a positive control -- still
    /// deletes it for real afterwards.
    /// </summary>
    [Test]
    [DisplayName("sqs.bad-receipt-handle")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.ErrorMessageText, "ReceiptHandleIsInvalid message is 'Receipt handle sim2real-bogus is invalid.', expected 'The input receipt handle is invalid.'")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-"), Tags = new(Tags) })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        const string body = "sim2real-bad-receipt-handle-marker";
        var sentId = (await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = body })).MessageId;
        var received = await PollReceiveOne(sqs, url, timeoutS: 30.0);
        Ensure(received.MessageId == sentId,
            $"queue delivered a different message ('{received.MessageId}') than the one " +
            $"sent ('{sentId}') -- the account is dirty and the legs below would be measured " +
            "against the wrong message");

        var error = await FidelityContext.CaptureError(() => sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = "sim2real-bogus" }));
        var code = error is null ? ((string?, string?)?)null : (error.Code, error.Message);
        Ensure(code == (ErrBadReceiptHandle, "The input receipt handle is invalid."),
            $"expected {ErrBadReceiptHandle}, got {code?.ToString() ?? "None"} " +
            "(None means the operation silently succeeded instead of raising)");

        // positive control: the SAME message's genuine, still-live handle deletes it for
        // real -- proving the rejection above was specific to the bogus handle rather than
        // every DeleteMessage call being refused (and, since the delete succeeds, that the
        // rejected call left the message and its handle untouched).
        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = received.ReceiptHandle });

        // terminal read: independent of both the send and the deletes, at the destination.
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(20.0))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 });
            Ensure(resp.Messages is null or { Count: 0 }, "message reappeared after being properly deleted");
        }
    }

    private static async Task<Message> PollReceiveOne(AmazonSQSClient sqs, string url, double timeoutS = 30.0)
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

// Translated from behavioral/sqs/check_receipt_handle_from_another_queue_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.receipt-handle-from-another-queue-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ReceiptHandleFromAnotherQueueRejected
{
    private const string ErrHandle = "ReceiptHandleIsInvalid";

    private static Dictionary<string, string> Tags() => new() { ["sim2real:managed"] = "true", ["sim2real"] = "cross-queue-receipt-handle" };

    /// <summary>
    /// A live receipt handle minted by queue A is refused by queue B with ReceiptHandleIsInvalid.
    /// A's message is not deleted and is redelivered; the next handle deletes it from A for real.
    /// </summary>
    [Test]
    [DisplayName("sqs.receipt-handle-from-another-queue-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var marker = $"xq-marker-{Guid.NewGuid().ToString("N")[..8]}";

        // VisibilityTimeout=2 makes the "still there" leg a quick redelivery.
        var urlA = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-") + "-a",
            Attributes = new() { ["VisibilityTimeout"] = "2" },
            Tags = Tags(),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(urlA));

        var urlB = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-") + "-b",
            Attributes = new() { ["VisibilityTimeout"] = "2" },
            Tags = Tags(),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(urlB));

        var sentId = (await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = urlA, MessageBody = marker })).MessageId;
        var first = await ReceiveOne(sqs, urlA, timeoutS: 60);
        Ensure(first is not null,
            "the message was never delivered off queue A, so there is no live receipt handle to "
            + $"present to queue B: MessageId='{sentId}'");
        Ensure(first.MessageId == sentId,
            $"queue A delivered a different message ('{first.MessageId}') than the one sent "
            + $"('{sentId}') -- the account is dirty and the legs below would be measured against the "
            + "wrong message");
        var handleA = first.ReceiptHandle;

        var (code, message) = await Err(() => sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = urlB, ReceiptHandle = handleA }));
        Ensure(code == ErrHandle,
            $"a receipt handle minted by another queue must be refused with '{ErrHandle}', got "
            + $"'{code}'");
        Ensure(message == HandleReason(handleA),
            "the refusal must be attributed to the handle belonging to another QUEUE, not merely to "
            + $"a malformed or expired handle: got '{message}'");

        var second = await ReceiveOne(sqs, urlA, timeoutS: 60);
        Ensure(second is not null,
            "queue A's message did not come back after its 2s VisibilityTimeout -- the DeleteMessage "
            + "aimed at queue B deleted it from queue A");
        Ensure(second.MessageId == sentId,
            $"queue A redelivered '{second.MessageId}' rather than the message under test "
            + $"('{sentId}')");
        Ensure(second.Body == marker,
            $"queue A's redelivered body was mutated by the cross-queue call: '{second.Body}'");
        Ensure(int.Parse(second.Attributes!["ApproximateReceiveCount"]) == 2,
            "the redelivery is not the second delivery of the same message: "
            + $"ApproximateReceiveCount='{second.Attributes["ApproximateReceiveCount"]}'");

        // Control: a refusal here raises out of the call: the check cannot pass by refusing every DeleteMessage.
        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = urlA, ReceiptHandle = second.ReceiptHandle });
        var reappeared = await ReceiveOne(sqs, urlA, timeoutS: 20, waitS: 5);
        Ensure(reappeared is null,
            "the message was still redelivered after being deleted from its own queue: "
            + $"'{reappeared?.Body}'");

        var bMsgs = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = urlB, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 })).Messages ?? [];
        Ensure(bMsgs.Count == 0,
            $"queue B holds messages it never received a send for: [{string.Join(", ", bMsgs.Select(m => m.Body))}]");
    }

    /// <summary>The exact message, attributing the refusal to the queue rather than to a malformed handle.</summary>
    private static string HandleReason(string handle) => $"The receipt handle \"{handle}\" is not valid for this queue.";

    /// <summary>Return (Error.Code, Error.Message) for the ClientError fn raises; a success raises.</summary>
    private static async Task<(string? Code, string Message)> Err<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it SUCCEEDED and returned {result}");
        }

        return (error.Code, error.Message ?? "");
    }

    /// <summary>The next message off <paramref name="url"/>, or None. Never deletes.</summary>
    private static async Task<Message?> ReceiveOne(IAmazonSQS sqs, string url, double timeoutS, int waitS = 2)
    {
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var batch = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url,
                MaxNumberOfMessages = 1,
                WaitTimeSeconds = waitS,
                AttributeNames = ["All"],
                MessageSystemAttributeNames = ["All"],
            })).Messages ?? [];
            if (batch.Count > 0)
            {
                return batch[0];
            }
        }

        return null;
    }
}

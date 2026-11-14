// Translated from behavioral/sqs/check_change_visibility_on_expired_handle_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.change-visibility-on-expired-handle-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ChangeVisibilityOnExpiredHandleRejected
{
    /// <summary>
    /// ChangeMessageVisibility using a receipt handle for a message that has already been
    /// deleted is rejected (the handle no longer references an in-flight message), while the
    /// same operation on a still-in-flight message succeeds.
    /// </summary>
    [Test]
    [DisplayName("sqs.change-visibility-on-expired-handle-rejected")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.ReceiptHandles, "ChangeMessageVisibility with a deleted message's receipt handle succeeds instead of InvalidParameterValue")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var messageId = (await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "deleted-then-revisibilitied" })).MessageId;

        var first = (await PollUntil(
            async () => (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 5 })).Messages ?? [],
            msgs => msgs.Count == 1,
            timeoutS: 20.0))[0];
        Ensure(first.MessageId == messageId, $"expected '{messageId}', got '{first.MessageId}'");
        var handle = first.ReceiptHandle;

        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = handle });

        var (code, message) = await Error(() => sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest { QueueUrl = url, ReceiptHandle = handle, VisibilityTimeout = 30 }));
        Ensure(code == "InvalidParameterValue",
            $"expected an error changing visibility via a deleted message's handle, got '{code}'");
        var expectedMessage =
            $"Value {handle} for parameter ReceiptHandle is invalid. " +
            "Reason: Message does not exist or is not available for visibility timeout change.";
        Ensure(message == expectedMessage, $"expected message '{expectedMessage}', got '{message}'");

        // The rejected call must not resurrect the deleted message.
        for (var i = 0; i < 3; i++)
        {
            var leftover = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 1 })).Messages ?? [];
            Ensure(!leftover.Any(m => m.MessageId == messageId),
                $"deleted message {messageId} reappeared after rejected ChangeMessageVisibility");
        }

        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "still-in-flight" });
        var second = (await PollUntil(
            async () => (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 5 })).Messages ?? [],
            msgs => msgs.Count == 1,
            timeoutS: 20.0))[0];
        await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest { QueueUrl = url, ReceiptHandle = second.ReceiptHandle, VisibilityTimeout = 30 });
        await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest { QueueUrl = url, ReceiptHandle = second.ReceiptHandle, VisibilityTimeout = 0 });
        var cleanupMsg = (await PollUntil(
            async () => (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 3 })).Messages ?? [],
            msgs => msgs.Count == 1,
            timeoutS: 10.0))[0];
        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = cleanupMsg.ReceiptHandle });
    }

    /// <summary>Return (Error.Code, Error.Message) of the ClientError fn raises; raise if fn succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail("expected the call to be refused with an error, but it succeeded");
    }

    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutS = 30.0, double intervalS = 1.0)
    {
        var deadline = Stopwatch.StartNew();
        T? last = default;
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"condition not met within {timeoutS}s (last={last})");
    }
}

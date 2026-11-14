// Translated from behavioral/sqs/check_change_message_visibility_invalid_receipt_handle.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.change-message-visibility-invalid-receipt-handle</summary>
[Category("AwsEvaluationSuite")]
public sealed class ChangeMessageVisibilityInvalidReceiptHandle
{
    /// <summary>
    /// ChangeMessageVisibility with a bogus ReceiptHandle fails with ReceiptHandleIsInvalid and does
    /// not extend the real message (it reappears on its short timeout); the real handle then succeeds.
    /// </summary>
    [Test]
    [DisplayName("sqs.change-message-visibility-invalid-receipt-handle")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.ErrorMessageText, "expected message 'The input receipt handle is invalid.', got 'Receipt handle totally-bogus-handle is invalid.'")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var (_, url) = await CreateQueue(ctx, attributes: new() { ["VisibilityTimeout"] = "5" });

        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "cmv-invalid-handle-probe" });
        var first = (await PollUntil(
            async () => (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 5 })).Messages ?? [],
            msgs => msgs.Count == 1,
            timeoutS: 20.0,
            description: "initial receive of the probe message"))[0];

        // Break exactly one thing: a syntactically bogus receipt handle.
        var (code, message) = await Error(() => sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest
        {
            QueueUrl = url,
            ReceiptHandle = "totally-bogus-handle",
            VisibilityTimeout = 60,
        }));
        Ensure(code == "ReceiptHandleIsInvalid", $"expected ReceiptHandleIsInvalid, got '{code}'");
        Ensure(message == "The input receipt handle is invalid.",
            $"expected message 'The input receipt handle is invalid.', got '{message}'");

        // The queue's own 5s timeout still governs the message, so it reappears well before the
        // rejected 60s extension would have allowed.
        var second = (await PollUntil(
            async () => (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 5 })).Messages ?? [],
            msgs => msgs.Count == 1 && msgs[0].MessageId == first.MessageId,
            timeoutS: 30.0,
            description: "the probe message reappearing on its real (5s) visibility timeout"))[0];

        // Positive control: the same call with the message's real receipt handle succeeds.
        await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest
        {
            QueueUrl = url,
            ReceiptHandle = second.ReceiptHandle,
            VisibilityTimeout = 5,
        });
        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = second.ReceiptHandle });
    }

    /// <summary>Call fn() and return (Error.Code, Error.Message); an unexpected success raises.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail("expected ChangeMessageVisibility with a bogus ReceiptHandle to be REFUSED, but it " +
                   $"SUCCEEDED: {result}");
    }

    /// <summary>Create a queue, register its teardown, and return (name, queue_url).</summary>
    private static async Task<(string Name, string Url)> CreateQueue(FidelityContext ctx, bool fifo = false, Dictionary<string, string>? attributes = null)
    {
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-sqs-check-");
        if (fifo)
        {
            name = $"{name}.fifo";
        }

        var attrs = new Dictionary<string, string>(attributes ?? []);
        if (fifo)
        {
            attrs.TryAdd("FifoQueue", "true");
        }

        var request = new CreateQueueRequest { QueueName = name };
        if (attrs.Count > 0)
        {
            request.Attributes = attrs;
        }

        var url = (await sqs.CreateQueueAsync(request)).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        return (name, url);
    }

    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutS, double intervalS = 1.0, string description = "condition")
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

        throw Fail($"{description} not observed within {timeoutS}s; last saw {last}");
    }
}

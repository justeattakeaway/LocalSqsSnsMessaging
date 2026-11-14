// Translated from behavioral/sqs/check_delete_message_invalid_receipt_handle_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.delete-message-invalid-receipt-handle-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteMessageInvalidReceiptHandleRejected
{
    private static readonly Dictionary<string, string> Tags = new() { ["sim2real:managed"] = "true", ["sim2real"] = "delete-message-invalid-receipt-handle-rejected" };

    /// <summary>
    /// DeleteMessage with a syntactically bogus receipt handle is rejected with
    /// ReceiptHandleIsInvalid; an independent read then confirms the real message was left
    /// untouched, and deleting it with its own (still-valid) receipt handle afterwards succeeds.
    /// </summary>
    [Test]
    [DisplayName("sqs.delete-message-invalid-receipt-handle-rejected")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-sqs-badhandle-"), Tags = new(Tags) })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        const string body = "protected-by-real-handle";
        var messageId = (await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = body })).MessageId;
        var msg = await PollReceiveOne(sqs, url, timeoutSeconds: 15.0);
        Ensure(msg.Body == body, $"expected body {body}, got {msg.Body}");
        Ensure(msg.MessageId == messageId, $"expected message {messageId}, got {msg.MessageId}");

        var (code, message) = await Error(
            () => sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = "totally-bogus-receipt-handle" }));
        Ensure(code == "ReceiptHandleIsInvalid", $"expected ReceiptHandleIsInvalid, got {code}");
        Ensure(message == "The input receipt handle is invalid.",
            $"expected message 'The input receipt handle is invalid.', got {message}");

        // effect absent, by an INDEPENDENT read: force the message visible again (this is not the
        // delete under test) and receive it fresh, proving the rejected call above did not touch it.
        await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle, VisibilityTimeout = 0 });
        var stillThere = await PollUntilMatching(sqs, url, messageId, timeoutSeconds: 15.0);

        // positive control: the untouched message can be deleted normally with a valid receipt handle.
        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = stillThere.ReceiptHandle });

        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed.TotalSeconds < 10.0)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 });
            Ensure(resp.Messages is null or { Count: 0 }, "message reappeared after being properly deleted");
        }
    }

    /// <summary>Return (Error.Code, Error.Message) of the ClientError fn raises; raise if fn succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected DeleteMessage to be REFUSED, but it SUCCEEDED: {JsonSerializer.Serialize(result, result!.GetType())}");
    }

    private static async Task<Message> PollReceiveOne(AmazonSQSClient sqs, string url, double timeoutSeconds = 15.0)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed.TotalSeconds < timeoutSeconds)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 3 });
            var messages = resp.Messages ?? [];
            if (messages.Count > 0)
            {
                return messages[0];
            }
        }

        throw Fail($"no message received within {timeoutSeconds}s");
    }

    private static async Task<Message> PollUntilMatching(AmazonSQSClient sqs, string url, string messageId, double timeoutSeconds = 15.0)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed.TotalSeconds < timeoutSeconds)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 3 });
            foreach (var candidate in resp.Messages ?? [])
            {
                if (candidate.MessageId == messageId)
                {
                    return candidate;
                }
            }
        }

        throw Fail(
            $"message {messageId} not observed within {timeoutSeconds}s after being made visible again");
    }
}

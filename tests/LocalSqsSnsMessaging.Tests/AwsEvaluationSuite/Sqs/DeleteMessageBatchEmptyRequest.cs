// Translated from behavioral/sqs/check_delete_message_batch_empty_request.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.delete-message-batch-empty-request</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteMessageBatchEmptyRequest
{
    private const string Check = "delete-message-batch-empty-request";

    // Empirically-confirmed SQS error codes (real AWS returns these legacy
    // "AWS.SimpleQueueService.*" prefixed codes, not the bare Smithy shape names).
    private const string EmptyBatch = "AWS.SimpleQueueService.EmptyBatchRequest";

    /// <summary>
    /// DeleteMessageBatch with a real entry succeeds (positive control); with an empty
    /// Entries list it fails with EmptyBatchRequest.
    /// </summary>
    [Test]
    [DisplayName("sqs.delete-message-batch-empty-request")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var (_, url) = await CreateQueue(ctx);

        // Positive control: DeleteMessageBatch against this queue works for a real entry,
        // so the empty-Entries rejection below cannot be mistaken for "rejects everything".
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = FidelityContext.UniqueName("sim2real-behav-sqs-check-msg-") });
        string? receipt = null;
        for (var i = 0; i < 10; i++)
        {
            var msgs = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 2 })).Messages ?? [];
            if (msgs.Count > 0)
            {
                receipt = msgs[0].ReceiptHandle;
                break;
            }
        }

        if (receipt is null)
        {
            throw Fail("expected to receive the message sent for the positive control");
        }

        var positive = await sqs.DeleteMessageBatchAsync(new DeleteMessageBatchRequest
        {
            QueueUrl = url,
            Entries = [new() { Id = "1", ReceiptHandle = receipt }],
        });
        Ensure(positive.Failed is null or { Count: 0 }, $"positive-control DeleteMessageBatch failed: {JsonSerializer.Serialize(positive.Failed)}");

        var (code, message) = await Error(() => sqs.DeleteMessageBatchAsync(new DeleteMessageBatchRequest { QueueUrl = url, Entries = [] }));
        Ensure(code == EmptyBatch, $"expected {EmptyBatch}, got {code}");
        const string expectedMessage = "There should be at least one DeleteMessageBatchRequestEntry in the request.";
        Ensure(message == expectedMessage, $"expected message {expectedMessage}, got {message}");
    }

    /// <summary>Call fn(); return (Error.Code, Error.Message) of the ClientError it raises; raise if it succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail("expected the call to be refused with an error, but it succeeded");
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

        var request = new CreateQueueRequest { QueueName = name, Tags = new() { ["sim2real-check"] = Check } };
        if (attrs.Count > 0)
        {
            request.Attributes = attrs;
        }

        var url = (await sqs.CreateQueueAsync(request)).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        return (name, url);
    }
}

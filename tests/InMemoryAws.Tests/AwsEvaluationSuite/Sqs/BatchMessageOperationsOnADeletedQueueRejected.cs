// Translated from behavioral/sqs/check_batch_message_operations_on_a_deleted_queue_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.batch-message-operations-on-a-deleted-queue-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchMessageOperationsOnADeletedQueueRejected
{
    private const string Check = "batch-message-operations-on-a-deleted-queue-rejected";
    private const string NotFound = "AWS.SimpleQueueService.NonExistentQueue";
    private const string NotFoundMsg = "The specified queue does not exist.";
    private const double SettleSeconds = 60.0;

    /// <summary>
    /// ChangeMessageVisibilityBatch and DeleteMessageBatch, built from receipt handles minted
    /// before the queue was deleted, are each refused with NonExistentQueue once an independent
    /// read already reports the queue gone, while a sibling queue still accepts both batch calls.
    /// </summary>
    [Test]
    [DisplayName("sqs.batch-message-operations-on-a-deleted-queue-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var @base = FidelityContext.UniqueName("sim2real-behav-batchdel-");
        var sibling = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = @base + "-kept", Tags = new() { ["sim2real"] = Check } })).QueueUrl;
        ctx.OnCleanup(() => DeleteQuietly(sqs, sibling));
        var doomed = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = @base + "-gone", Tags = new() { ["sim2real"] = Check } })).QueueUrl;
        ctx.OnCleanup(() => DeleteQuietly(sqs, doomed));

        // Positive control: both batch calls succeed on a live queue.
        var controlReceipts = await ReceiveTwo(sqs, sibling, "control");
        var visResp = await sqs.ChangeMessageVisibilityBatchAsync(new ChangeMessageVisibilityBatchRequest
        {
            QueueUrl = sibling,
            Entries = [.. controlReceipts.Select((r, i) => new ChangeMessageVisibilityBatchRequestEntry { Id = $"c{i}", ReceiptHandle = r, VisibilityTimeout = 5 })],
        });
        Ensure(visResp.Failed is not { Count: > 0 },
            $"positive control ChangeMessageVisibilityBatch failed entries: {ShowFailed(visResp.Failed)}");
        var delResp = await sqs.DeleteMessageBatchAsync(new DeleteMessageBatchRequest
        {
            QueueUrl = sibling,
            Entries = [.. controlReceipts.Select((r, i) => new DeleteMessageBatchRequestEntry { Id = $"d{i}", ReceiptHandle = r })],
        });
        Ensure(delResp.Failed is not { Count: > 0 },
            $"positive control DeleteMessageBatch failed entries: {ShowFailed(delResp.Failed)}");

        var deadline = FidelityTime.StartNew();
        List<Message>? left = null;
        while (deadline.Elapsed < TimeSpan.FromSeconds(SettleSeconds))
        {
            left = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = sibling, MaxNumberOfMessages = 10, WaitTimeSeconds = 3 })).Messages ?? [];
            if (left.Count == 0)
            {
                break;
            }
        }

        Ensure(left is { Count: 0 },
            $"positive control DeleteMessageBatch left messages behind: [{string.Join(", ", (left ?? []).Select(m => m.Body))}]");

        var doomedReceipts = await ReceiveTwo(sqs, doomed, "doomed");
        await sqs.DeleteQueueAsync(doomed);

        // Deletion is eventually consistent; poll an independent read (GetQueueAttributes, not the
        // batch operations) until it reports the queue gone, then hold both batch calls to the same
        // refusal on handles minted before the delete.
        AwsError? readErr = null;
        deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(SettleSeconds))
        {
            readErr = await FidelityContext.CaptureError(() => sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = doomed, AttributeNames = ["QueueArn"] }));
            if (readErr is not null)
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(3));
        }

        Ensure((readErr?.Code, readErr?.Message) == (NotFound, NotFoundMsg),
            $"GetQueueAttributes on the deleted queue answered {readErr?.ToString() ?? "{}"} within {SettleSeconds}s");

        var visErr = await Error(() => sqs.ChangeMessageVisibilityBatchAsync(new ChangeMessageVisibilityBatchRequest
        {
            QueueUrl = doomed,
            Entries = [.. doomedReceipts.Select((r, i) => new ChangeMessageVisibilityBatchRequestEntry { Id = $"v{i}", ReceiptHandle = r, VisibilityTimeout = 5 })],
        }));
        Ensure((visErr.Code, visErr.Message) == (NotFound, NotFoundMsg),
            $"ChangeMessageVisibilityBatch on the deleted queue answered {visErr}");

        var delErr = await Error(() => sqs.DeleteMessageBatchAsync(new DeleteMessageBatchRequest
        {
            QueueUrl = doomed,
            Entries = [.. doomedReceipts.Select((r, i) => new DeleteMessageBatchRequestEntry { Id = $"x{i}", ReceiptHandle = r })],
        }));
        Ensure((delErr.Code, delErr.Message) == (NotFound, NotFoundMsg),
            $"DeleteMessageBatch on the deleted queue answered {delErr}");
    }

    /// <summary>Send two messages and return the ReceiptHandles of both deliveries.</summary>
    private static async Task<List<string>> ReceiveTwo(AmazonSQSClient sqs, string url, string prefix)
    {
        var bodies = new HashSet<string> { $"{prefix}-0", $"{prefix}-1" };
        foreach (var body in bodies)
        {
            await sqs.SendMessageAsync(url, body);
        }

        var receipts = new Dictionary<string, string>();
        var deadline = FidelityTime.StartNew();
        while (receipts.Count < bodies.Count && deadline.Elapsed < TimeSpan.FromSeconds(SettleSeconds))
        {
            var msgs = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 3 })).Messages ?? [];
            foreach (var m in msgs)
            {
                if (bodies.Contains(m.Body))
                {
                    receipts[m.Body] = m.ReceiptHandle;
                }
            }
        }

        Ensure(receipts.Count == bodies.Count,
            $"only received [{string.Join(", ", receipts.Keys.Order(StringComparer.Ordinal))}] of [{string.Join(", ", bodies.Order(StringComparer.Ordinal))}] on '{url}' within {SettleSeconds}s");
        return [.. receipts.Values];
    }

    private static string ShowFailed(List<BatchResultErrorEntry>? failed) =>
        "[" + string.Join(", ", (failed ?? []).Select(f => $"{{'Id': '{f.Id}', 'Code': '{f.Code}', 'Message': '{f.Message}'}}")) + "]";

    /// <summary>The botocore Error dict <paramref name="fn"/> raises. An unexpected success raises, naming the result.</summary>
    private static async Task<AwsError> Error(Func<Task> fn)
        => await FidelityContext.CaptureError(fn) ?? throw Fail("expected the call to be refused, but it succeeded");

    private static async Task DeleteQuietly(AmazonSQSClient sqs, string url)
    {
        try
        {
            await sqs.DeleteQueueAsync(url);
        }
        catch (AmazonServiceException exc) when (exc.ErrorCode == NotFound)
        {
        }
    }
}

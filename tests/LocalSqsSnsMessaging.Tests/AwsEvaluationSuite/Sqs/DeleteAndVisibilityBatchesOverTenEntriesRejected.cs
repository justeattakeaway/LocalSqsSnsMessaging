// Translated from behavioral/sqs/check_delete_and_visibility_batches_over_ten_entries_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.delete-and-visibility-batches-over-ten-entries-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteAndVisibilityBatchesOverTenEntriesRejected
{
    private static readonly Dictionary<string, string> Tags = new() { ["sim2real"] = "delete-and-visibility-batches-over-ten-entries-rejected" };
    private const string TooMany = "AWS.SimpleQueueService.TooManyEntriesInBatchRequest";
    private const string TooManyMsg = "Maximum number of entries per request are 10. You have sent 11.";
    private const string Empty = "AWS.SimpleQueueService.EmptyBatchRequest";
    private const string EmptyCmvMsg = "There should be at least one ChangeMessageVisibilityBatchRequestEntry in the request.";

    /// <summary>
    /// DeleteMessageBatch and ChangeMessageVisibilityBatch refuse 11 entries with
    /// TooManyEntriesInBatchRequest and act on none; an empty visibility batch is EmptyBatchRequest;
    /// 10-entry batches succeed, so released messages return and deleted ones stay gone.
    /// </summary>
    [Test]
    [DisplayName("sqs.delete-and-visibility-batches-over-ten-entries-rejected")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.Batch, "an 11-entry DeleteMessageBatch succeeded instead of TooManyEntriesInBatchRequest")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-batch11-"),
            Attributes = new() { ["VisibilityTimeout"] = "120" },
            Tags = new(Tags),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        for (var chunk = 0; chunk < 3; chunk++)
        {
            await sqs.SendMessageBatchAsync(new SendMessageBatchRequest
            {
                QueueUrl = url,
                Entries = [.. Enumerable.Range(0, 7).Select(k => new SendMessageBatchRequestEntry { Id = $"m{k}", MessageBody = $"msg-{chunk}-{k}" })],
            });
        }

        var held = await Take(sqs, url, 21);
        Ensure(held.Count == 21, $"could take only {held.Count} of 21 messages in flight");
        var (first11, rest) = (held[..11], held[11..]);

        var err = await Error(() => sqs.DeleteMessageBatchAsync(new DeleteMessageBatchRequest
        {
            QueueUrl = url,
            Entries = [.. first11.Select((m, i) => new DeleteMessageBatchRequestEntry { Id = $"d{i}", ReceiptHandle = m.ReceiptHandle })],
        }));
        Ensure((err.Code, err.Message) == (TooMany, TooManyMsg),
            $"an 11-entry DeleteMessageBatch answered {err}");
        err = await Error(() => sqs.ChangeMessageVisibilityBatchAsync(new ChangeMessageVisibilityBatchRequest
        {
            QueueUrl = url,
            Entries = [.. first11.Select((m, i) => new ChangeMessageVisibilityBatchRequestEntry { Id = $"v{i}", ReceiptHandle = m.ReceiptHandle, VisibilityTimeout = 0 })],
        }));
        Ensure((err.Code, err.Message) == (TooMany, TooManyMsg),
            $"an 11-entry ChangeMessageVisibilityBatch answered {err}");
        err = await Error(() => sqs.ChangeMessageVisibilityBatchAsync(new ChangeMessageVisibilityBatchRequest { QueueUrl = url, Entries = [] }));
        Ensure((err.Code, err.Message) == (Empty, EmptyCmvMsg),
            $"an empty ChangeMessageVisibilityBatch answered {err}");

        // All 21 are still in flight, so an immediate receive sees nothing.
        var leaked = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 3 });
        Ensure((leaked.Messages ?? []).Count == 0,
            $"after refused batches a receive returned [{string.Join(", ", leaked.Messages!.Select(m => m.Body))}]; " +
            "the refused 11-entry visibility batch released messages");

        // The first 11 handles still being valid shows the refused delete deleted none of them.
        var (toDelete, toRelease) = (first11[..10], rest[..10]);
        var resp = await sqs.DeleteMessageBatchAsync(new DeleteMessageBatchRequest
        {
            QueueUrl = url,
            Entries = [.. toDelete.Select((m, i) => new DeleteMessageBatchRequestEntry { Id = $"d{i}", ReceiptHandle = m.ReceiptHandle })],
        });
        Ensure((resp.Successful ?? []).Count == 10 && resp.Failed is null or { Count: 0 },
            $"a 10-entry DeleteMessageBatch answered {Repr(resp.Failed)}");
        var resp2 = await sqs.ChangeMessageVisibilityBatchAsync(new ChangeMessageVisibilityBatchRequest
        {
            QueueUrl = url,
            Entries = [.. toRelease.Select((m, i) => new ChangeMessageVisibilityBatchRequestEntry { Id = $"v{i}", ReceiptHandle = m.ReceiptHandle, VisibilityTimeout = 0 })],
        });
        Ensure((resp2.Successful ?? []).Count == 10 && resp2.Failed is null or { Count: 0 },
            $"a 10-entry ChangeMessageVisibilityBatch answered {Repr(resp2.Failed)}");
        var again = (await Take(sqs, url, 10)).Select(m => m.MessageId).ToHashSet();
        Ensure(again.SetEquals(toRelease.Select(m => m.MessageId)),
            $"after releasing ten messages, {again.Count} were received again and " +
            $"{again.Intersect(toDelete.Select(m => m.MessageId)).Count()} of them were among the deleted ten");
    }

    /// <summary>The botocore Error dict <paramref name="fn"/> raises. An unexpected success raises, naming the result.</summary>
    private static async Task<AwsError> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return error;
        }

        throw Fail($"expected the batch to be refused, but it succeeded: {Repr(result)}");
    }

    private static async Task<List<Message>> Take(AmazonSQSClient sqs, string url, int n)
    {
        var held = new Dictionary<string, Message>();
        var deadline = Stopwatch.StartNew();
        while (held.Count < n && deadline.Elapsed.TotalSeconds < 45)
        {
            foreach (var m in (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
                     {
                         QueueUrl = url,
                         MaxNumberOfMessages = 10,
                         WaitTimeSeconds = 2,
                         VisibilityTimeout = 120,
                     })).Messages ?? [])
            {
                held[m.MessageId] = m;
            }
        }

        return [.. held.Values];
    }

    private static string Repr(object? value)
    {
        try
        {
            return JsonSerializer.Serialize(value, value?.GetType() ?? typeof(object));
        }
        catch (Exception)
        {
            return value?.ToString() ?? "None";
        }
    }
}

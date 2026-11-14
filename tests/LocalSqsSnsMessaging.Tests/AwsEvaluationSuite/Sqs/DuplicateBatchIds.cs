// Translated from behavioral/sqs/check_duplicate_batch_ids.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using System.Text.Json;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.duplicate-batch-ids</summary>
[Category("AwsEvaluationSuite")]
public sealed class DuplicateBatchIds
{
    private const string ErrBatchIdsNotDistinct = "AWS.SimpleQueueService.BatchEntryIdsNotDistinct";
    private const string MsgBatchIdsNotDistinct = "Id dup repeated.";

    /// <summary>
    /// SendMessageBatch with duplicate entry Ids is rejected wholesale with
    /// BatchEntryIdsNotDistinct, none of the duplicate-Id entries enqueue, and an
    /// otherwise-identical batch with distinct Ids on the same queue succeeds in full.
    /// </summary>
    [Test]
    [DisplayName("sqs.duplicate-batch-ids")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.Batch, "a SendMessageBatch with duplicate entry Ids succeeded instead of BatchEntryIdsNotDistinct")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var dupEntries = new List<SendMessageBatchRequestEntry>
        {
            new() { Id = "dup", MessageBody = "first" },
            new() { Id = "dup", MessageBody = "second" },
        };
        var error = await ClientError(() => sqs.SendMessageBatchAsync(new SendMessageBatchRequest { QueueUrl = url, Entries = dupEntries }));
        Ensure(error.Code == ErrBatchIdsNotDistinct,
            $"expected {ErrBatchIdsNotDistinct}, got {error.Code}");
        Ensure(error.Message == MsgBatchIdsNotDistinct,
            $"expected message {MsgBatchIdsNotDistinct}, got {error.Message}");

        // effect absent: the wholesale rejection must not have enqueued either entry
        var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 });
        Ensure(resp.Messages is null or { Count: 0 },
            $"messages were enqueued despite the rejected duplicate-Id batch: [{string.Join(", ", (resp.Messages ?? []).Select(m => m.Body))}]");

        // positive control: the identical bodies with distinct Ids on the same queue succeed in full
        var distinctEntries = new List<SendMessageBatchRequestEntry>
        {
            new() { Id = "one", MessageBody = "first" },
            new() { Id = "two", MessageBody = "second" },
        };
        var resp2 = await sqs.SendMessageBatchAsync(new SendMessageBatchRequest { QueueUrl = url, Entries = distinctEntries });
        Ensure((resp2.Successful ?? []).Count == 2, $"expected 2 successes, got {Repr(resp2)}");
        Ensure(resp2.Failed is null or { Count: 0 }, $"unexpected per-entry failures: {Repr(resp2.Failed)}");

        var delivered = new List<string>();
        var deadline = Stopwatch.StartNew();
        while (delivered.Count < 2 && deadline.Elapsed.TotalSeconds < 20.0)
        {
            var r = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 3 });
            foreach (var msg in r.Messages ?? [])
            {
                delivered.Add(msg.Body);
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle });
            }
        }

        Ensure(delivered.Order(StringComparer.Ordinal).SequenceEqual(["first", "second"]),
            $"expected both distinct-Id messages delivered, got [{string.Join(", ", delivered)}]");
    }

    /// <summary>The botocore Error dict of a rejected call; an unexpected success raises.</summary>
    private static async Task<AwsError> ClientError<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return error;
        }

        throw Fail($"expected the call to be rejected, but it SUCCEEDED and returned {Repr(result)}");
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

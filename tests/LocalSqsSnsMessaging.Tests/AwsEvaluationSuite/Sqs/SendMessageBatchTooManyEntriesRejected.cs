// Translated from behavioral/sqs/check_send_message_batch_too_many_entries_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.send-message-batch-too-many-entries-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class SendMessageBatchTooManyEntriesRejected
{
    /// <summary>
    /// SendMessageBatch with more than the 10-entry maximum is rejected wholesale with
    /// TooManyEntriesInBatchRequest (none of the 11 messages land), while a batch at the
    /// 10-entry limit succeeds in full.
    /// </summary>
    [Test]
    [DisplayName("sqs.send-message-batch-too-many-entries-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-sqs-batch-limit-"),
            Tags = new() { ["sim2real:managed"] = "true", ["sim2real"] = "send-message-batch-too-many-entries-rejected" },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var oversizedEntries = Enumerable.Range(0, 11).Select(i => new SendMessageBatchRequestEntry { Id = $"m{i}", MessageBody = $"body-{i}" }).ToList();
        var (code, message) = await Error(() => sqs.SendMessageBatchAsync(new SendMessageBatchRequest { QueueUrl = url, Entries = oversizedEntries }));
        // empirically, real AWS returns the wire error code prefixed with the service
        // namespace: "AWS.SimpleQueueService.TooManyEntriesInBatchRequest".
        Ensure(code == "AWS.SimpleQueueService.TooManyEntriesInBatchRequest",
            $"expected AWS.SimpleQueueService.TooManyEntriesInBatchRequest, got '{code}'");
        Ensure(message == "Maximum number of entries per request are 10. You have sent 11.",
            $"unexpected refusal message '{message}'");

        var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 });
        Ensure(resp.Messages is null or { Count: 0 }, "messages were enqueued despite the rejected oversized batch");

        // positive control: exactly 10 entries is within the limit and succeeds in full
        var validEntries = Enumerable.Range(0, 10).Select(i => new SendMessageBatchRequestEntry { Id = $"ok{i}", MessageBody = $"ok-body-{i}" }).ToList();
        var batchResp = await sqs.SendMessageBatchAsync(new SendMessageBatchRequest { QueueUrl = url, Entries = validEntries });
        Ensure((batchResp.Successful ?? []).Count == 10,
            $"expected all 10 valid entries to succeed, got {(batchResp.Successful ?? []).Count}");
        Ensure((batchResp.Failed ?? []).Count == 0, $"valid 10-entry batch reported failures: {(batchResp.Failed ?? []).Count}");

        var delivered = 0;
        var deadline = FidelityTime.StartNew();
        while (delivered < 10 && deadline.Elapsed < TimeSpan.FromSeconds(20.0))
        {
            var r = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 3 });
            foreach (var msg in r.Messages ?? [])
            {
                delivered++;
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle });
            }
        }

        Ensure(delivered == 10, $"expected 10 delivered messages from the valid batch, got {delivered}");
    }

    /// <summary>
    /// Call fn(); return its (Error.Code, Error.Message). An unexpected SUCCESS raises, because an
    /// emulator ACCEPTING input real AWS refuses is the strongest signal available.
    /// </summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"real AWS rejects this call, but it SUCCEEDED and returned {result}");
        }

        return (error.Code, error.Message);
    }
}

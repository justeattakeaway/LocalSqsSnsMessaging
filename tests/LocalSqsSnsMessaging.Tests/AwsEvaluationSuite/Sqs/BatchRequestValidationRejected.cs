// Translated from behavioral/sqs/check_batch_request_validation_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.batch-request-validation-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchRequestValidationRejected
{
    private const string EmptyBatch = "AWS.SimpleQueueService.EmptyBatchRequest";
    private const string TooManyEntries = "AWS.SimpleQueueService.TooManyEntriesInBatchRequest";
    private const string BatchIdsNotDistinct = "AWS.SimpleQueueService.BatchEntryIdsNotDistinct";

    /// <summary>
    /// SendMessageBatch rejects empty, over-10-entry and duplicate-Id batches with distinct codes and
    /// enqueues none of them, while a well-formed batch on the same queue still succeeds and delivers.
    /// </summary>
    [Test]
    [DisplayName("sqs.batch-request-validation-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var codeEmpty = await ErrCode(() => sqs.SendMessageBatchAsync(new SendMessageBatchRequest { QueueUrl = url, Entries = [] }));
        Ensure(codeEmpty == (EmptyBatch, "There should be at least one SendMessageBatchRequestEntry in the request."),
            $"expected {EmptyBatch}, got {codeEmpty}");
        await AssertNothingEnqueued(sqs, url, reason: "empty batch");

        var tooMany = Enumerable.Range(0, 11).Select(i => new SendMessageBatchRequestEntry { Id = $"id{i}", MessageBody = "x" }).ToList();
        var codeMany = await ErrCode(() => sqs.SendMessageBatchAsync(new SendMessageBatchRequest { QueueUrl = url, Entries = tooMany }));
        Ensure(codeMany == (TooManyEntries, "Maximum number of entries per request are 10. You have sent 11."),
            $"expected {TooManyEntries}, got {codeMany}");
        await AssertNothingEnqueued(sqs, url, reason: "11-entry batch");

        List<SendMessageBatchRequestEntry> dup = [new() { Id = "same", MessageBody = "a" }, new() { Id = "same", MessageBody = "b" }];
        var codeDup = await ErrCode(() => sqs.SendMessageBatchAsync(new SendMessageBatchRequest { QueueUrl = url, Entries = dup }));
        Ensure(codeDup == (BatchIdsNotDistinct, "Id same repeated."), $"expected {BatchIdsNotDistinct}, got {codeDup}");
        await AssertNothingEnqueued(sqs, url, reason: "duplicate-id batch");

        // Positive control: the same queue still accepts and delivers a well-formed batch, so the
        // codes above are specific rejections, not a broken queue.
        List<SendMessageBatchRequestEntry> valid = [new() { Id = "ok-0", MessageBody = "ok-0-body" }, new() { Id = "ok-1", MessageBody = "ok-1-body" }];
        var resp = await sqs.SendMessageBatchAsync(new SendMessageBatchRequest { QueueUrl = url, Entries = valid });
        Ensure(resp.Failed is null or { Count: 0 }, $"positive control failed: [{string.Join(", ", (resp.Failed ?? []).Select(f => f.Id))}]");
        Ensure((resp.Successful ?? []).Select(e => e.Id).ToHashSet().SetEquals(["ok-0", "ok-1"]),
            $"expected both ok-0 and ok-1 to succeed, got [{string.Join(", ", (resp.Successful ?? []).Select(e => e.Id))}]");

        var delivered = new Dictionary<string, string>();
        var deadline = FidelityTime.StartNew();
        while (delivered.Count < 2 && deadline.Elapsed < TimeSpan.FromSeconds(20.0))
        {
            var r = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 3 });
            foreach (var msg in r.Messages ?? [])
            {
                delivered[msg.Body] = msg.ReceiptHandle;
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle });
            }
        }

        if (delivered.Count < 2)
        {
            throw Fail($"expected both positive-control messages delivered, got [{string.Join(", ", delivered.Keys.Order(StringComparer.Ordinal))}]");
        }

        Ensure(delivered.Keys.ToHashSet().SetEquals(["ok-0-body", "ok-1-body"]),
            $"expected both positive-control messages delivered, got [{string.Join(", ", delivered.Keys.Order(StringComparer.Ordinal))}]");
    }

    /// <summary>
    /// Call fn() and return the botocore (Error.Code, Error.Message) it raises; an unexpected
    /// success raises.
    /// </summary>
    private static async Task<(string? Code, string? Message)> ErrCode<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the batch to be rejected, but it SUCCEEDED: {result}");
    }

    private static async Task AssertNothingEnqueued(AmazonSQSClient sqs, string url, string reason)
    {
        var leftover = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 })).Messages ?? [];
        Ensure(leftover.Count == 0, $"{reason}: expected nothing enqueued, got {leftover.Count} message(s)");
    }
}

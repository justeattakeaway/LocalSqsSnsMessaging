// Translated from behavioral/sqs/check_batch_send_partial_mixed.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.batch-send-partial-mixed</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchSendPartialMixed
{
    private const string ErrBatchEmptyValue = "EmptyValue";

    /// <summary>
    /// SendMessageBatch with one valid and one empty-body entry succeeds for the valid one,
    /// which is then received, and fails the empty one with EmptyValue "No Value Found for
    /// SendMessageBatchRequestEntry.2.MessageBody.".
    /// </summary>
    [Test]
    [DisplayName("sqs.batch-send-partial-mixed")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.Batch, "an empty-body SendMessageBatch entry succeeds: expected only 'ok' successful, got ['empty', 'ok']")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        List<SendMessageBatchRequestEntry> entries =
        [
            new() { Id = "ok", MessageBody = "valid-body" },
            new() { Id = "empty", MessageBody = "" },
        ];
        var resp = await sqs.SendMessageBatchAsync(new SendMessageBatchRequest { QueueUrl = url, Entries = entries });
        var successful = (resp.Successful ?? []).Select(e => e.Id).ToHashSet();
        var failed = (resp.Failed ?? []).ToDictionary(e => e.Id);
        if (!successful.SetEquals(["ok"]))
        {
            throw Fail($"expected only 'ok' successful, got [{string.Join(", ", successful.Order(StringComparer.Ordinal))}]");
        }

        if (!failed.Keys.ToHashSet().SetEquals(["empty"]))
        {
            throw Fail($"expected only 'empty' failed, got [{string.Join(", ", failed.Keys.Order(StringComparer.Ordinal))}]");
        }

        if (failed["empty"].Code != ErrBatchEmptyValue)
        {
            throw Fail($"expected Failed code {ErrBatchEmptyValue}, got '{failed["empty"].Code}'");
        }

        const string expected = "No Value Found for SendMessageBatchRequestEntry.2.MessageBody.";
        if (failed["empty"].Message != expected)
        {
            throw Fail($"expected Failed message '{expected}', got '{failed["empty"].Message}'");
        }

        // The valid entry must actually be enqueued (the failed sibling did not poison the batch) —
        // proved by an independent read at the destination, not by the send response alone.
        var goodMessageId = resp.Successful![0].MessageId;
        var received = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 10 })).Messages ?? [];
        var match = received.FirstOrDefault(m => m.MessageId == goodMessageId);
        if (match is null)
        {
            throw Fail($"expected message {goodMessageId} (entry 'ok') to be receivable from the queue, " +
                       $"got [{string.Join(", ", received.Select(m => m.MessageId))}]");
        }

        if (match.Body != "valid-body")
        {
            throw Fail($"expected received body 'valid-body', got '{match.Body}'");
        }

        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = match.ReceiptHandle });
    }
}

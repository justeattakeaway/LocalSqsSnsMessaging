// Translated from behavioral/sqs/check_send_message_batch_duplicate_ids_rejected_atomically.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.send-message-batch-duplicate-ids-rejected-atomically</summary>
[Category("AwsEvaluationSuite")]
public sealed class SendMessageBatchDuplicateIdsRejectedAtomically
{
    /// <summary>
    /// SendMessageBatch entries sharing the same Id are rejected wholesale (BatchEntryIdsNotDistinct);
    /// none of the entries land, unlike a per-entry Failed/Successful split. Distinct Ids succeed fully.
    /// </summary>
    [Test]
    [DisplayName("sqs.send-message-batch-duplicate-ids-rejected-atomically")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        // While the bus accepts the duplicate-Id batch, the .NET SDK's own response validation
        // (ValidationResponseHandler.ValidateSendMessageBatch) throws ArgumentException on the repeated
        // Id; botocore has no such check, so the Python reports the success instead. Either way the
        // check fails, and once the bus refuses the batch the SDK never reaches that validation.
        var (code, message) = await Error(() => sqs.SendMessageBatchAsync(new SendMessageBatchRequest
        {
            QueueUrl = url,
            Entries =
            [
                new() { Id = "dup", MessageBody = "first-copy" },
                new() { Id = "dup", MessageBody = "second-copy" },
            ],
        }));
        Ensure(code == "AWS.SimpleQueueService.BatchEntryIdsNotDistinct",
            $"expected AWS.SimpleQueueService.BatchEntryIdsNotDistinct, got '{code}'");
        Ensure(message == "Id dup repeated.", $"expected message 'Id dup repeated.', got '{message}'");

        // Effect absent: this is a whole-request rejection, not a per-entry partial failure --
        // neither message body must have landed on the queue.
        var leftover = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 })).Messages ?? [];
        Ensure(leftover.Count == 0, $"expected no messages to have landed, found [{string.Join(", ", leftover.Select(m => m.Body))}]");

        // Positive control: the same two bodies with distinct Ids succeed in full.
        var resp = await sqs.SendMessageBatchAsync(new SendMessageBatchRequest
        {
            QueueUrl = url,
            Entries =
            [
                new() { Id = "a", MessageBody = "first-copy" },
                new() { Id = "b", MessageBody = "second-copy" },
            ],
        });
        Ensure((resp.Successful ?? []).Count == 2, $"expected 2 Successful entries, got {(resp.Successful ?? []).Count}");
        Ensure((resp.Failed ?? []).Count == 0, $"expected no Failed entries, got {(resp.Failed ?? []).Count}");

        // Terminal assertion is an independent read at the destination, not the mutating call's
        // own response: both distinct-Id bodies must actually be visible on the queue.
        var landed = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 })).Messages ?? [];
        var landedBodies = landed.Select(m => m.Body).Order(StringComparer.Ordinal).ToList();
        Ensure(landedBodies.SequenceEqual(["first-copy", "second-copy"]),
            $"expected both distinct-Id bodies to have landed, got [{string.Join(", ", landedBodies)}]");
    }

    /// <summary>Return (Error.Code, Error.Message) of the ClientError fn raises; raise if fn succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (_, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail("expected the call to be refused with an error, but it succeeded");
        }

        return (error.Code, error.Message);
    }
}

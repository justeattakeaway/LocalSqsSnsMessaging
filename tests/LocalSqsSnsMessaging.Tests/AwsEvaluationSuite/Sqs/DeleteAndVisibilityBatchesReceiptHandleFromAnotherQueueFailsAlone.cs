// Translated from behavioral/sqs/check_delete_and_visibility_batches_receipt_handle_from_another_queue_fails_alone.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.delete-and-visibility-batches-receipt-handle-from-another-queue-fails-alone</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteAndVisibilityBatchesReceiptHandleFromAnotherQueueFailsAlone
{
    private static readonly Dictionary<string, string> Tags = new() { ["sim2real"] = "delete-and-visibility-batches-receipt-handle-from-another-queue-fails-alone" };
    private const string ErrHandle = "ReceiptHandleIsInvalid";

    /// <summary>
    /// A batch entry whose receipt handle was minted by another queue fails alone with
    /// ReceiptHandleIsInvalid and a queue-scoped message, distinct from the malformed-handle message;
    /// the sibling entry with a local handle succeeds and the foreign queue's message is untouched.
    /// </summary>
    [Test]
    [DisplayName("sqs.delete-and-visibility-batches-receipt-handle-from-another-queue-fails-alone")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var urlA = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-crossq-a-"),
            Tags = new(Tags),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(urlA));
        var urlB = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-crossq-b-"),
            Tags = new(Tags),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(urlB));

        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = urlB, MessageBody = "minted-on-b" });
        var foreign = await ReceiveOne(sqs, urlB, visibilityTimeout: 120);
        var foreignHandle = foreign.ReceiptHandle;

        // --- DeleteMessageBatch: the foreign handle fails alone, the local one is really deleted.
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = urlA, MessageBody = "delete-me-on-a" });
        var localForDelete = await ReceiveOne(sqs, urlA, visibilityTimeout: 120);

        var resp = await sqs.DeleteMessageBatchAsync(new DeleteMessageBatchRequest
        {
            QueueUrl = urlA,
            Entries =
            [
                new() { Id = "local", ReceiptHandle = localForDelete.ReceiptHandle },
                new() { Id = "foreign", ReceiptHandle = foreignHandle },
            ],
        });
        var ok = (resp.Successful ?? []).Select(e => e.Id).ToHashSet();
        var failed = (resp.Failed ?? []).GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.Last());
        Ensure(ok.SetEquals(["local"]), $"DeleteMessageBatch Successful Ids were [{string.Join(", ", ok)}], expected just 'local'");
        Ensure(failed.Keys.ToHashSet().SetEquals(["foreign"]), $"DeleteMessageBatch Failed Ids were [{string.Join(", ", failed.Keys)}]");
        Ensure(failed["foreign"].Code == ErrHandle,
            $"foreign-handle Delete entry Code was {failed["foreign"].Code}");
        Ensure(failed["foreign"].Message == HandleReason(foreignHandle),
            $"foreign-handle Delete entry Message was {failed["foreign"].Message}");
        Ensure(failed["foreign"].SenderFault is true,
            $"foreign-handle Delete entry SenderFault was {failed["foreign"].SenderFault}");
        var gone = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = urlA, MaxNumberOfMessages = 10, WaitTimeSeconds = 3 });
        Ensure((gone.Messages ?? []).Count == 0,
            $"after the batch, url_a still held [{string.Join(", ", (gone.Messages ?? []).Select(m => m.Body))}]; " +
            "the local entry's delete must really have taken effect");

        // --- ChangeMessageVisibilityBatch: the foreign handle fails alone, the local one is released.
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = urlA, MessageBody = "release-me-on-a" });
        var localForCmv = await ReceiveOne(sqs, urlA, visibilityTimeout: 120);

        var resp2 = await sqs.ChangeMessageVisibilityBatchAsync(new ChangeMessageVisibilityBatchRequest
        {
            QueueUrl = urlA,
            Entries =
            [
                new() { Id = "local", ReceiptHandle = localForCmv.ReceiptHandle, VisibilityTimeout = 0 },
                new() { Id = "foreign", ReceiptHandle = foreignHandle, VisibilityTimeout = 0 },
            ],
        });
        ok = (resp2.Successful ?? []).Select(e => e.Id).ToHashSet();
        failed = (resp2.Failed ?? []).GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.Last());
        Ensure(ok.SetEquals(["local"]), $"CMV batch Successful Ids were [{string.Join(", ", ok)}], expected just 'local'");
        Ensure(failed.Keys.ToHashSet().SetEquals(["foreign"]), $"CMV batch Failed Ids were [{string.Join(", ", failed.Keys)}]");
        Ensure(failed["foreign"].Code == ErrHandle,
            $"foreign-handle CMV entry Code was {failed["foreign"].Code}");
        Ensure(failed["foreign"].Message == HandleReason(foreignHandle),
            $"foreign-handle CMV entry Message was {failed["foreign"].Message}");
        var released = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = urlA, MaxNumberOfMessages = 10, WaitTimeSeconds = 3 });
        var releasedBodies = (released.Messages ?? []).Select(m => m.Body).ToList();
        Ensure(releasedBodies.SequenceEqual(["release-me-on-a"]),
            $"after the batch, an immediate receive on url_a returned [{string.Join(", ", releasedBodies)}]; the " +
            "local entry's VisibilityTimeout=0 must really have released the message");
        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = urlA, ReceiptHandle = released.Messages![0].ReceiptHandle });

        // The foreign message on url_b was never touched by either refused entry.
        var stillThere = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = urlB, MaxNumberOfMessages = 10, WaitTimeSeconds = 3 });
        Ensure((stillThere.Messages ?? []).Count == 0,
            $"url_b answered [{string.Join(", ", (stillThere.Messages ?? []).Select(m => m.Body))}] to an " +
            "immediate receive; its message is still in flight under foreign_handle and must not " +
            "have been affected by either refused batch entry");
    }

    /// <summary>Message AWS returns for a well-formed handle scoped to a different queue.</summary>
    private static string HandleReason(string handle) => $"The receipt handle \"{handle}\" is not valid for this queue.";

    private static async Task<Message> ReceiveOne(AmazonSQSClient sqs, string url, int visibilityTimeout, double timeoutSeconds = 20.0)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed.TotalSeconds < timeoutSeconds)
        {
            var msgs = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url,
                MaxNumberOfMessages = 1,
                WaitTimeSeconds = 2,
                VisibilityTimeout = visibilityTimeout,
            })).Messages ?? [];
            if (msgs.Count > 0)
            {
                return msgs[0];
            }
        }

        throw Fail($"no message receivable on {url} within {timeoutSeconds}s");
    }
}

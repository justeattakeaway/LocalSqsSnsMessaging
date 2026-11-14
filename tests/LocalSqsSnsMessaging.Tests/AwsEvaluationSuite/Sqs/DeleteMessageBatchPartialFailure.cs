// Translated from behavioral/sqs/check_delete_message_batch_partial_failure.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.delete-message-batch-partial-failure</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteMessageBatchPartialFailure
{
    private const string Check = "delete-message-batch-partial-failure";
    private static readonly Dictionary<string, string> Tags = new() { ["sim2real:managed"] = "true", ["sim2real"] = Check };

    /// <summary>
    /// DeleteMessageBatch is per-entry: a cross-queue receipt handle fails with
    /// ReceiptHandleIsInvalid while the valid one succeeds; both queues are re-read to confirm.
    /// </summary>
    [Test]
    [DisplayName("sqs.delete-message-batch-partial-failure")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-"),
            Attributes = new() { ["VisibilityTimeout"] = "2" },
            Tags = new(Tags),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var otherUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-"),
            Attributes = new() { ["VisibilityTimeout"] = "2" },
            Tags = new(Tags),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(otherUrl));

        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "m1" });
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "m2" });
        var msgs = await ReceiveMessages(sqs, url, minCount: 2, attempts: 20);
        Ensure(msgs.Count >= 2, $"expected 2 messages, got {msgs.Count}");
        var (id1, id2) = (msgs[0].MessageId, msgs[1].MessageId);
        var (handle1, handle2) = (msgs[0].ReceiptHandle, msgs[1].ReceiptHandle);
        _ = handle2;

        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = otherUrl, MessageBody = "other" });
        var otherMsgs = await ReceiveMessages(sqs, otherUrl, minCount: 1, maxBatch: 1, attempts: 20);
        Ensure(otherMsgs.Count > 0, "other-queue message never became visible");
        var otherId = otherMsgs[0].MessageId;
        var otherHandle = otherMsgs[0].ReceiptHandle;

        var entries = new List<DeleteMessageBatchRequestEntry>
        {
            new() { Id = "d1", ReceiptHandle = handle1 },
            new() { Id = "d2", ReceiptHandle = otherHandle },
        };
        var resp = await sqs.DeleteMessageBatchAsync(new DeleteMessageBatchRequest { QueueUrl = url, Entries = entries });
        var successfulIds = (resp.Successful ?? []).Select(s => s.Id).ToHashSet();
        var failed = (resp.Failed ?? []).GroupBy(f => f.Id).ToDictionary(g => g.Key, g => g.Last());

        Ensure(successfulIds.SetEquals(["d1"]), $"unexpected successes: [{string.Join(", ", successfulIds)}]");
        Ensure(failed.ContainsKey("d2"), $"cross-queue handle unexpectedly accepted: {JsonSerializer.Serialize(resp.Successful)}");
        Ensure(failed["d2"].Code == "ReceiptHandleIsInvalid", $"got {failed["d2"].Code}");
        var expectedMessage = $"The receipt handle \"{otherHandle}\" is not valid for this queue.";
        Ensure(failed["d2"].Message == expectedMessage,
            $"expected message {expectedMessage}, got {failed["d2"].Message}");

        // Independent read at the destination: wait out the 2s VisibilityTimeout on `url` and see what
        // actually redelivers. Only m2 (id2, never touched by the batch call) may reappear -- if m1
        // (id1) is also still there, the "Successful" d1 entry was a response-only claim.
        var redelivered = await ReceiveMessages(sqs, url, minCount: 1, maxBatch: 10, attempts: 20);
        var redeliveredIds = redelivered.Select(m => m.MessageId).ToHashSet();
        Ensure(redeliveredIds.SetEquals([id2]),
            $"expected only m2 ({id2}) to redeliver from {url} after the batch delete, got " +
            $"[{string.Join(", ", redeliveredIds)}]; if {id1} reappeared the 'Successful' d1 entry did not really delete it");
        var freshHandle2 = redelivered.First(m => m.MessageId == id2).ReceiptHandle;

        // Independent read at the destination the failed entry actually named: the "other" message must
        // still be sitting in other_url, unaffected by a batch call it was never truly part of.
        var stillThere = await ReceiveMessages(sqs, otherUrl, minCount: 1, maxBatch: 1, attempts: 20);
        var stillThereIds = stillThere.Select(m => m.MessageId).ToHashSet();
        Ensure(stillThereIds.SetEquals([otherId]),
            $"expected the undeleted 'other' message ({otherId}) to still be in {otherUrl}, got " +
            $"[{string.Join(", ", stillThereIds)}]; a ReceiptHandleIsInvalid response that deleted the message anyway " +
            "would be worse than an outright acceptance");
        var freshOtherHandle = stillThere.First(m => m.MessageId == otherId).ReceiptHandle;

        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = freshHandle2 });
        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = otherUrl, ReceiptHandle = freshOtherHandle });
    }

    private static async Task<List<Message>> ReceiveMessages(AmazonSQSClient sqs, string url, int minCount, int maxBatch = 10, int attempts = 15)
    {
        var received = new List<Message>();
        var seen = new HashSet<string>();
        var idle = 0;
        for (var i = 0; i < attempts; i++)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = maxBatch, WaitTimeSeconds = 1 });
            var msgs = resp.Messages ?? [];
            if (msgs.Count == 0)
            {
                idle++;
                if (received.Count > 0 && idle >= 3)
                {
                    break;
                }

                continue;
            }

            idle = 0;
            foreach (var m in msgs)
            {
                if (seen.Add(m.MessageId))
                {
                    received.Add(m);
                }
            }

            if (received.Count >= minCount)
            {
                break;
            }
        }

        return received;
    }
}

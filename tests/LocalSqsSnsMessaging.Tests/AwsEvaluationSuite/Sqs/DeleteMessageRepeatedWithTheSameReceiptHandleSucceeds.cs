// Translated from behavioral/sqs/check_delete_message_repeated_with_the_same_receipt_handle_succeeds.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.delete-message-repeated-with-the-same-receipt-handle-succeeds</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteMessageRepeatedWithTheSameReceiptHandleSucceeds
{
    private const string Scenario = "sqs-repeated-delete";

    /// <summary>
    /// A repeated DeleteMessage and a DeleteMessageBatch entry with an already-used receipt handle
    /// both succeed and the message never returns, while an undeleted sibling reappears after its
    /// short visibility timeout, proving the window passed.
    /// </summary>
    [Test]
    [DisplayName("sqs.delete-message-repeated-with-the-same-receipt-handle-succeeds")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = await CreateQueue(ctx, new() { ["VisibilityTimeout"] = "3" });
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "sim2real-delete-me" });
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "sim2real-keep-me" });

        var handles = new Dictionary<string, string>();
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed.TotalSeconds < 30.0 && handles.Count < 2)
        {
            foreach (var msg in (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 })).Messages ?? [])
            {
                handles[msg.Body] = msg.ReceiptHandle;
            }
        }

        var sortedHandles = handles.Keys.Order(StringComparer.Ordinal).ToList();
        Ensure(sortedHandles.SequenceEqual(["sim2real-delete-me", "sim2real-keep-me"]),
            $"both messages must be received once, got [{string.Join(", ", sortedHandles)}]");

        var handle = handles["sim2real-delete-me"];
        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = handle });
        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = handle });
        var batch = await sqs.DeleteMessageBatchAsync(new DeleteMessageBatchRequest
        {
            QueueUrl = url,
            Entries = [new() { Id = "again", ReceiptHandle = handle }],
        });
        Ensure((batch.Successful ?? []).Select(e => e.Id).SequenceEqual(["again"]) && (batch.Failed ?? []).Count == 0,
            "a DeleteMessageBatch entry for an already-deleted message must be Successful, got " +
            $"Successful={JsonSerializer.Serialize(batch.Successful)} Failed={JsonSerializer.Serialize(batch.Failed)}");

        var seen = new List<string>();
        deadline = FidelityTime.StartNew();
        while (deadline.Elapsed.TotalSeconds < 60.0 && !seen.Contains("sim2real-keep-me"))
        {
            foreach (var msg in (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 })).Messages ?? [])
            {
                seen.Add(msg.Body);
                if (msg.Body == "sim2real-keep-me")
                {
                    await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle });
                }
            }
        }

        Ensure(seen.Contains("sim2real-keep-me"),
            "the undeleted sibling never reappeared after its 3s visibility timeout");
        Ensure(!seen.Contains("sim2real-delete-me"), "the deleted message was delivered again");
        var leftover = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = url,
            MaxNumberOfMessages = 10,
            WaitTimeSeconds = 5,
            VisibilityTimeout = 0,
        })).Messages ?? [];
        Ensure(leftover.Count == 0,
            $"the queue must be empty once the sibling is deleted, got [{string.Join(", ", leftover.Select(m => m.Body))}]");
    }

    /// <summary>CreateQueue (tagged + prefix-named) and register DeleteQueue immediately.</summary>
    private static async Task<string> CreateQueue(FidelityContext ctx, Dictionary<string, string>? attributes = null)
    {
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-sqs-"),
            Attributes = attributes ?? [],
            Tags = new() { ["sim2real"] = Scenario, ["sim2real:managed"] = "true" },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        return url;
    }
}

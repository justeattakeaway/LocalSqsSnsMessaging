// Translated from behavioral/sqs/check_change_message_visibility_batch_repeated_receipt_handle_last_entry_wins.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.change-message-visibility-batch-repeated-receipt-handle-last-entry-wins</summary>
[Category("AwsEvaluationSuite")]
public sealed class ChangeMessageVisibilityBatchRepeatedReceiptHandleLastEntryWins
{
    private static readonly Dictionary<string, string> Tags = new() { ["sim2real"] = "change-message-visibility-batch-repeated-receipt-handle-last-entry-wins" };

    /// <summary>
    /// ChangeMessageVisibilityBatch entries naming the same receipt handle under distinct Ids both
    /// succeed with no Failed, in either order; which timeout takes effect is not graded because the
    /// docs do not say in what order entries apply.
    /// </summary>
    [Test]
    [DisplayName("sqs.change-message-visibility-batch-repeated-receipt-handle-last-entry-wins")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-cmvdup-"),
            Attributes = new() { ["VisibilityTimeout"] = "20" },
            Tags = new(Tags),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        foreach (var (leg, order) in new[] { ("leg1", new[] { "extend", "release" }), ("leg2", new[] { "release", "extend" }) })
        {
            var body = $"cmvdup-{leg}";
            await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = body });
            var msg = await ReceiveOne(sqs, url, visibilityTimeout: 20);
            Ensure(msg.Body == body, $"receive got '{msg.Body}', expected '{body}'");
            var handle = msg.ReceiptHandle;
            var timeouts = new Dictionary<string, int> { ["extend"] = 60, ["release"] = 0 };
            var resp = await sqs.ChangeMessageVisibilityBatchAsync(new ChangeMessageVisibilityBatchRequest
            {
                QueueUrl = url,
                Entries = order.Select(entryId => new ChangeMessageVisibilityBatchRequestEntry { Id = entryId, ReceiptHandle = handle, VisibilityTimeout = timeouts[entryId] }).ToList(),
            });
            var ok = (resp.Successful ?? []).Select(e => e.Id).ToHashSet();
            Ensure(ok.SetEquals(["extend", "release"]) && resp.Failed is null or { Count: 0 },
                $"{leg}: a batch with two Ids sharing one receipt handle answered " +
                $"Successful=[{string.Join(", ", (resp.Successful ?? []).Select(e => e.Id))}] Failed=[{string.Join(", ", (resp.Failed ?? []).Select(e => $"{e.Id}:{e.Code}"))}]");
            // Delete the message so the next leg receives its own message, not this one.
            await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = handle });
        }
    }

    private static async Task<Message> ReceiveOne(AmazonSQSClient sqs, string url, int visibilityTimeout, double timeoutS = 20.0)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var msgs = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 2, VisibilityTimeout = visibilityTimeout,
            })).Messages ?? [];
            if (msgs.Count > 0)
            {
                return msgs[0];
            }
        }

        throw Fail($"no message receivable within {timeoutS}s");
    }
}

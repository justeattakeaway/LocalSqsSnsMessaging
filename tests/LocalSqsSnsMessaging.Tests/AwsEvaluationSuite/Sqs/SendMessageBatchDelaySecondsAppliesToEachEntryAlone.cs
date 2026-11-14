// Translated from behavioral/sqs/check_send_message_batch_delay_seconds_applies_to_each_entry_alone.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.send-message-batch-delay-seconds-applies-to-each-entry-alone</summary>
[Category("AwsEvaluationSuite")]
public sealed class SendMessageBatchDelaySecondsAppliesToEachEntryAlone
{
    private const string Scenario = "sqs-batch-entry-delay";
    private const int Delay = 8;

    /// <summary>
    /// In one SendMessageBatch each entry's DelaySeconds governs only that entry: the undelayed
    /// entry arrives on an earlier receive, the 8-second one not before 8 seconds after the send,
    /// and each keeps its own MessageAttributes.
    /// </summary>
    [Test]
    [DisplayName("sqs.send-message-batch-delay-seconds-applies-to-each-entry-alone")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = await CreateQueue(ctx);
        var sentAt = Stopwatch.StartNew();
        var resp = await sqs.SendMessageBatchAsync(new SendMessageBatchRequest
        {
            QueueUrl = url,
            Entries =
            [
                new()
                {
                    Id = "now",
                    MessageBody = "sim2real-now",
                    MessageAttributes = new() { ["slot"] = new() { DataType = "String", StringValue = "immediate" } },
                },
                new()
                {
                    Id = "later",
                    MessageBody = "sim2real-later",
                    DelaySeconds = Delay,
                    MessageAttributes = new() { ["slot"] = new() { DataType = "String", StringValue = "delayed" } },
                },
            ],
        });
        Ensure((resp.Successful ?? []).Select(e => e.Id).Order(StringComparer.Ordinal).SequenceEqual(["later", "now"])
               && (resp.Failed ?? []).Count == 0,
            $"both entries must be accepted, got [{string.Join(", ", (resp.Failed ?? []).Select(f => $"{f.Id}:{f.Code}"))}]");

        Dictionary<string, (int Call, double At, string Slot)> arrivals = [];
        var call = 0;
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(60.0) && arrivals.Count < 2)
        {
            call++;
            var msgs = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 1,
                MessageAttributeNames = ["All"],
            })).Messages ?? [];
            foreach (var msg in msgs)
            {
                arrivals[msg.Body] = (call, sentAt.Elapsed.TotalSeconds, msg.MessageAttributes!["slot"].StringValue);
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle });
            }
        }

        var sortedArrivals = arrivals.Keys.Order(StringComparer.Ordinal).ToList();
        Ensure(sortedArrivals.SequenceEqual(["sim2real-later", "sim2real-now"]),
            $"both entries must eventually be delivered, got [{string.Join(", ", sortedArrivals)}]");
        var (nowCall, _, nowSlot) = arrivals["sim2real-now"];
        var (laterCall, laterAt, laterSlot) = arrivals["sim2real-later"];
        Ensure((nowSlot, laterSlot) == ("immediate", "delayed"),
            $"each entry must keep its own attributes, got '{nowSlot}' and '{laterSlot}'");
        Ensure(nowCall < laterCall,
            "the undelayed entry must be handed out by an earlier receive than the delayed one; "
            + $"they arrived on receive #{nowCall} and #{laterCall}, so the delay held both back");
        Ensure(laterAt >= Delay,
            $"the entry with DelaySeconds={Delay} was delivered {laterAt:F1}s after the send");
    }

    // The Python module's unused helpers (_error, _receive_all, _receive_window) are not ported.

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

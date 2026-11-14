// Translated from behavioral/sqs/check_change_message_visibility_batch_partial.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.change-message-visibility-batch-partial</summary>
[Category("AwsEvaluationSuite")]
public sealed class ChangeMessageVisibilityBatchPartial
{
    private const string Check = "change-message-visibility-batch-partial";

    /// <summary>
    /// ChangeMessageVisibilityBatch applies per-entry: a valid handle succeeds while a bogus handle fails.
    /// </summary>
    [Test]
    [DisplayName("sqs.change-message-visibility-batch-partial")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-"),
            Attributes = new() { ["VisibilityTimeout"] = "5" },
            Tags = new() { ["sim2real"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "cmvb-1" });
        var msgs = await ReceiveMessages(sqs, url, minCount: 1, maxBatch: 1, attempts: 20);
        Ensure(msgs.Count > 0, "message never became visible");
        var goodHandle = msgs[0].ReceiptHandle;

        List<ChangeMessageVisibilityBatchRequestEntry> entries =
        [
            new() { Id = "e1", ReceiptHandle = goodHandle, VisibilityTimeout = 60 },
            new() { Id = "e2", ReceiptHandle = "bogus-receipt-handle", VisibilityTimeout = 60 },
        ];
        var resp = await sqs.ChangeMessageVisibilityBatchAsync(new ChangeMessageVisibilityBatchRequest { QueueUrl = url, Entries = entries });
        var successful = (resp.Successful ?? []).Select(s => s.Id).ToHashSet();
        var failed = (resp.Failed ?? []).ToDictionary(f => f.Id);

        Ensure(successful.SetEquals(["e1"]), $"unexpected successes: {{{string.Join(", ", successful)}}}");
        Ensure(failed.ContainsKey("e2"), $"bogus handle unexpectedly accepted: {resp}");
        Ensure(failed["e2"].Code == "ReceiptHandleIsInvalid", $"got {failed["e2"].Code}");
        Ensure(failed["e2"].Message == "The input receipt handle is invalid.", $"got message '{failed["e2"].Message}'");

        // entry e1's extension must actually take effect: message stays hidden
        var hidden = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 3 })).Messages ?? [];
        Ensure(hidden.Count == 0, "message became visible despite batch visibility extension");

        await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest { QueueUrl = url, ReceiptHandle = goodHandle, VisibilityTimeout = 0 });
    }

    private static async Task<List<Message>> ReceiveMessages(AmazonSQSClient sqs, string url, int minCount, int maxBatch = 10, int attempts = 15)
    {
        var received = new List<Message>();
        var seen = new HashSet<string>();
        var idle = 0;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var r = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = maxBatch, WaitTimeSeconds = 1 });
            var msgs = r.Messages ?? [];
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

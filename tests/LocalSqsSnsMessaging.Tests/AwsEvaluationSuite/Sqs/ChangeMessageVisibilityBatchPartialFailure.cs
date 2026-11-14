// Translated from behavioral/sqs/check_change_message_visibility_batch_partial_failure.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.change-message-visibility-batch-partial-failure</summary>
[Category("AwsEvaluationSuite")]
public sealed class ChangeMessageVisibilityBatchPartialFailure
{
    /// <summary>
    /// ChangeMessageVisibilityBatch fails a malformed-handle entry on its own, leaving that message on
    /// its original timeout, while the valid sibling entry's extension takes effect.
    /// </summary>
    [Test]
    [DisplayName("sqs.change-message-visibility-batch-partial-failure")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-sqs-batch-cvt-"),
            Attributes = new() { ["VisibilityTimeout"] = "4" },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "cvt-batch-good" });
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "cvt-batch-bad" });

        var handles = new Dictionary<string, string>();
        var deadline = Stopwatch.StartNew();
        while (handles.Count < 2 && deadline.Elapsed < TimeSpan.FromSeconds(20.0))
        {
            var r = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 3 });
            foreach (var msg in r.Messages ?? [])
            {
                handles[msg.Body] = msg.ReceiptHandle;
            }
        }

        Ensure(handles.Keys.ToHashSet().SetEquals(["cvt-batch-good", "cvt-batch-bad"]),
            $"expected both messages received, got [{string.Join(", ", handles.Keys.Order(StringComparer.Ordinal))}]");

        var resp = await sqs.ChangeMessageVisibilityBatchAsync(new ChangeMessageVisibilityBatchRequest
        {
            QueueUrl = url,
            Entries =
            [
                new() { Id = "good", ReceiptHandle = handles["cvt-batch-good"], VisibilityTimeout = 20 },
                new() { Id = "bad", ReceiptHandle = "not-a-valid-handle", VisibilityTimeout = 20 },
            ],
        });
        var successfulIds = (resp.Successful ?? []).Select(e => e.Id).ToHashSet();
        var failed = (resp.Failed ?? []).ToDictionary(e => e.Id);
        Ensure(successfulIds.SetEquals(["good"]), $"expected only 'good' to succeed, got {{{string.Join(", ", successfulIds)}}}");
        Ensure(failed.Keys.ToHashSet().SetEquals(["bad"]), $"expected only 'bad' to fail, got {{{string.Join(", ", failed.Keys)}}}");
        Ensure(failed["bad"].Code == "ReceiptHandleIsInvalid", $"expected ReceiptHandleIsInvalid, got {failed["bad"].Code}");
        Ensure(failed["bad"].Message == "The input receipt handle is invalid.",
            $"expected message 'The input receipt handle is invalid.', got '{failed["bad"].Message}'");

        // Past the original 4s timeout but before the 20s extension: only "bad" (whose extension
        // failed) should reappear while "good" stays hidden.
        deadline = Stopwatch.StartNew();
        var foundBad = false;
        while (deadline.Elapsed < TimeSpan.FromSeconds(15.0) && !foundBad)
        {
            var r = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 3 });
            foreach (var msg in r.Messages ?? [])
            {
                Ensure(msg.Body != "cvt-batch-good", "extended message reappeared before its new timeout");
                if (msg.Body == "cvt-batch-bad")
                {
                    foundBad = true;
                    await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle });
                }
            }
        }

        Ensure(foundBad, "message whose visibility-extension failed never reappeared on schedule");

        // The successfully extended message reappears once its 20s window elapses.
        deadline = Stopwatch.StartNew();
        var goodSeen = false;
        while (deadline.Elapsed < TimeSpan.FromSeconds(25.0) && !goodSeen)
        {
            var r = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 3 });
            foreach (var msg in r.Messages ?? [])
            {
                if (msg.Body == "cvt-batch-good")
                {
                    goodSeen = true;
                    await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle });
                }
            }
        }

        Ensure(goodSeen, "successfully extended message never reappeared after its extension elapsed");
    }
}

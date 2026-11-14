// Translated from behavioral/sqs/check_send_message_batch_partial_failure.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.send-message-batch-partial-failure</summary>
[Category("AwsEvaluationSuite")]
public sealed class SendMessageBatchPartialFailure
{
    /// <summary>
    /// SendMessageBatch is not atomic: an entry with disallowed characters fails while a valid sibling succeeds.
    /// </summary>
    [Test]
    [DisplayName("sqs.send-message-batch-partial-failure")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-"),
            Tags = new() { ["sim2real:managed"] = "true", ["sim2real"] = "send-message-batch-partial-failure" },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var resp = await sqs.SendMessageBatchAsync(new SendMessageBatchRequest
        {
            QueueUrl = url,
            Entries =
            [
                new() { Id = "good", MessageBody = "this-is-fine" },
                new() { Id = "bad", MessageBody = "disallowed-char-\x01-here" },
            ],
        });

        var successful = (resp.Successful ?? []).ToDictionary(e => e.Id);
        var failed = (resp.Failed ?? []).ToDictionary(e => e.Id);

        Ensure(successful.Keys.ToHashSet().SetEquals(["good"]),
            $"Successful Ids: [{string.Join(", ", successful.Keys.Order(StringComparer.Ordinal).Select(k => $"'{k}'"))}]");
        Ensure(failed.Keys.ToHashSet().SetEquals(["bad"]),
            $"Failed Ids: [{string.Join(", ", failed.Keys.Order(StringComparer.Ordinal).Select(k => $"'{k}'"))}]");
        Ensure(failed["bad"].Code == "InvalidMessageContents", $"Failed Code: '{failed["bad"].Code}'");
        Ensure(failed["bad"].SenderFault is true,
            $"Failed SenderFault must be True for a client error, got {failed["bad"].SenderFault}");
        Ensure(failed["bad"].Message ==
               "Invalid binary character '#x1' was found in the message body, the set of allowed "
               + "characters is #x9 | #xA | #xD | [#x20-#xD7FF] | [#xE000-#xFFFD] | [#x10000-#x10FFFF].",
            $"Failed Message: '{failed["bad"].Message}'");

        // The valid entry must actually be enqueued (the failure did not poison the whole batch).
        var goodMessageId = successful["good"].MessageId;
        var received = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 10 })).Messages ?? [];
        Ensure(received.Any(m => m.MessageId == goodMessageId && m.Body == "this-is-fine"),
            $"expected message '{goodMessageId}' with body 'this-is-fine' among received: [{string.Join(", ", received.Select(m => m.MessageId))}]");
        foreach (var m in received)
        {
            if (m.MessageId == goodMessageId)
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle });
            }
        }
    }
}

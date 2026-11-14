// Translated from behavioral/sqs/check_receive_message_respects_max_number_of_messages_cap.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.receive-message-respects-max-number-of-messages-cap</summary>
[Category("AwsEvaluationSuite")]
public sealed class ReceiveMessageRespectsMaxNumberOfMessagesCap
{
    /// <summary>
    /// A single ReceiveMessage call never returns more messages than MaxNumberOfMessages, and drains fully.
    /// </summary>
    [Test]
    [DisplayName("sqs.receive-message-respects-max-number-of-messages-cap")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-"),
            Tags = new() { ["sim2real"] = "true" },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        const int total = 15;
        HashSet<string> sentIds = [];
        for (var i = 0; i < total; i++)
        {
            var resp = await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = $"cap-check-{i}" });
            sentIds.Add(resp.MessageId);
        }

        HashSet<string> receivedIds = [];
        var deadline = FidelityTime.StartNew();
        var emptyPolls = 0;
        while (receivedIds.Count < total && deadline.Elapsed < TimeSpan.FromSeconds(30.0) && emptyPolls < 5)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 5, WaitTimeSeconds = 3 });
            var batch = resp.Messages ?? [];
            Ensure(batch.Count <= 5, $"ReceiveMessage returned {batch.Count} messages, exceeding MaxNumberOfMessages=5");
            if (batch.Count == 0)
            {
                emptyPolls++;
                continue;
            }

            emptyPolls = 0;
            foreach (var m in batch)
            {
                receivedIds.Add(m.MessageId);
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle });
            }
        }

        Ensure(receivedIds.SetEquals(sentIds),
            $"expected to drain all {sentIds.Count} sent message ids, "
            + $"missing={{{string.Join(", ", sentIds.Except(receivedIds))}}}, unexpected={{{string.Join(", ", receivedIds.Except(sentIds))}}}");
    }
}

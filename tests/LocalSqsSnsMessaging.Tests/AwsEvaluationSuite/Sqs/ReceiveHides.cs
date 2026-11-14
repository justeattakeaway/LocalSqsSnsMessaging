// Translated from behavioral/sqs/check_receive_hides.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.receive-hides</summary>
[Category("AwsEvaluationSuite")]
public sealed class ReceiveHides
{
    /// <summary>
    /// A received message is in-flight: immediate second receive is empty.
    /// </summary>
    [Test]
    [DisplayName("sqs.receive-hides")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "receive-hides" });
        List<Message> received = [];
        HashSet<string> seen = [];
        var idle = 0;
        for (var _ = 0; _ < 12; _++)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 1 });
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

            if (received.Count >= 1)
            {
                break;
            }
        }

        if (received.Count == 0)
        {
            throw Fail("no message received");
        }

        var leftover = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 1 })).Messages ?? [];
        Ensure(leftover.Count == 0, $"expected no messages, got {leftover.Count}");

        // Independent side-channel, at a genuinely different operation than ReceiveMessage:
        // the approximate not-visible counter must confirm the same in-flight state (direction
        // only — it is eventually consistent, so poll rather than trust a single read).
        var notvis = "0";
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(20.0))
        {
            var attrs = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = url,
                AttributeNames = ["ApproximateNumberOfMessagesNotVisible"],
            })).Attributes!;
            notvis = attrs.GetValueOrDefault("ApproximateNumberOfMessagesNotVisible", "0");
            if (notvis != "0")
            {
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        Ensure(int.Parse(notvis) >= 1,
            $"ApproximateNumberOfMessagesNotVisible stayed at '{notvis}' while a message was in flight");
    }
}

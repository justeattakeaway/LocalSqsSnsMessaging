// Translated from behavioral/sqs/check_receive_max_messages.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.receive-max-messages</summary>
[Category("AwsEvaluationSuite")]
public sealed class ReceiveMaxMessages
{
    private const string Check = "receive-max-messages";

    /// <summary>
    /// Send 5 messages: every ReceiveMessage(MaxNumberOfMessages=3) batch is capped at 3, and
    /// all 5 messages are eventually observed (so an empty-result stub cannot pass vacuously).
    /// </summary>
    [Test]
    [DisplayName("sqs.receive-max-messages")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-"),
            Tags = new() { ["sim2real-check"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var bodies = Enumerable.Range(0, 5).Select(i => $"max-msg-{i}").ToHashSet();
        foreach (var body in bodies)
        {
            await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = body });
        }

        Dictionary<string, string> seen = [];
        var idle = 0;
        var deadline = Stopwatch.StartNew();
        while (seen.Count < bodies.Count && deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 3, WaitTimeSeconds = 2 });
            var batch = resp.Messages ?? [];
            if (batch.Count == 0)
            {
                idle++;
                if (idle >= 3)
                {
                    break;
                }

                continue;
            }

            idle = 0;
            Ensure(batch.Count <= 3, $"expected at most 3 messages per ReceiveMessage call, got {batch.Count}");
            foreach (var m in batch)
            {
                seen[m.MessageId] = m.Body;
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle });
            }
        }

        Ensure(seen.Values.ToHashSet().SetEquals(bodies),
            $"expected all of [{string.Join(", ", bodies.Order())}], got [{string.Join(", ", seen.Values.Order())}]");
    }
}

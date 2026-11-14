// Translated from behavioral/sqs/check_e2e_work_queue_pattern.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.e2e-work-queue-pattern</summary>
[Category("AwsEvaluationSuite")]
public sealed class E2eWorkQueuePattern
{
    private static readonly Dictionary<string, string> Tags = new() { ["sim2real:managed"] = "true", ["sim2real"] = "e2e-work-queue-pattern" };

    /// <summary>
    /// Full worker pattern: send 5 messages, receive-process-delete loop, verify empty.
    /// </summary>
    [Test]
    [DisplayName("sqs.e2e-work-queue-pattern")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-"), Tags = new(Tags) })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var token = Guid.NewGuid().ToString("N")[..8];
        var bodies = Enumerable.Range(0, 5).Select(i => $"work-{i}-{token}").ToHashSet();
        foreach (var body in bodies)
        {
            await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = body });
        }

        // Worker loop: receive, "process", delete
        var processed = new HashSet<string>();
        for (var i = 0; i < 20; i++)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 });
            var messages = resp.Messages ?? [];
            if (messages.Count == 0)
            {
                if (processed.SetEquals(bodies))
                {
                    break;
                }

                continue;
            }

            foreach (var msg in messages)
            {
                processed.Add(msg.Body);
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle });
            }

            if (processed.SetEquals(bodies))
            {
                break;
            }
        }

        if (!processed.SetEquals(bodies))
        {
            throw Fail(
                $"processed {processed.Count} messages, expected {bodies.Count}: " +
                $"missing=[{string.Join(", ", bodies.Except(processed))}]");
        }

        // Verify queue is empty via two independent observables: a direct receive and
        // the queue's own reported approximate count.
        var leftover = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 1 })).Messages ?? [];
        Ensure(leftover.Count == 0, $"expected no messages, got {leftover.Count}");
        var approx = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["ApproximateNumberOfMessages"] }))
            .Attributes!["ApproximateNumberOfMessages"];
        Ensure(approx == "0", $"expected ApproximateNumberOfMessages '0', got {approx}");
    }
}

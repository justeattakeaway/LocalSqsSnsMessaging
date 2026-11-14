// Translated from behavioral/sqs/check_fifo_group_isolation.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.fifo-group-isolation</summary>
[Category("AwsEvaluationSuite")]
public sealed class FifoGroupIsolation
{
    private const string Check = "fifo-group-isolation";

    /// <summary>
    /// An in-flight FIFO message hides the rest of its own group but not other groups: with A1, A2
    /// in group A and B1 in group B, single-message receives (no delete) return the heads A1 and B1.
    /// </summary>
    [Test]
    [DisplayName("sqs.fifo-group-isolation")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-") + ".fifo",
            Attributes = new() { ["FifoQueue"] = "true", ["ContentBasedDeduplication"] = "true" },
            Tags = new() { ["sim2real:managed"] = "true", ["sim2real"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        foreach (var (group, bodies) in new[] { ("grp-A", new[] { "A1", "A2" }), ("grp-B", new[] { "B1" }) })
        {
            foreach (var body in bodies)
            {
                await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = body, MessageGroupId = group });
            }
        }

        // MaxNumberOfMessages=1 so a single call cannot drain A1+A2 together.
        List<string> heads = [];
        for (var i = 0; i < 12; i++)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 1 });
            foreach (var m in resp.Messages ?? [])
            {
                if (!heads.Contains(m.Body))
                {
                    heads.Add(m.Body);
                }
            }

            if (heads.Count >= 2)
            {
                break;
            }
        }

        var sorted = heads.Order(StringComparer.Ordinal).ToList();
        Ensure(sorted.SequenceEqual(["A1", "B1"]),
            $"expected group heads A1,B1 receivable while in-flight (A2 blocked), got [{string.Join(", ", sorted)}]");
    }
}

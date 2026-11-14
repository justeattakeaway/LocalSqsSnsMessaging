// Translated from behavioral/sqs/check_fifo_dedup_scope_message_group_allows_same_dedup_id_in_different_groups.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.fifo-dedup-scope-message-group-allows-same-dedup-id-in-different-groups</summary>
[Category("AwsEvaluationSuite")]
public sealed class FifoDedupScopeMessageGroupAllowsSameDedupIdInDifferentGroups
{
    // `sim2real:managed=true` lets account cleanup find the queues via the Resource Groups Tagging API.
    private static readonly Dictionary<string, string> Tags = new() { ["sim2real:managed"] = "true", ["sim2real"] = "fifo-dedup-scope-message-group" };

    /// <summary>
    /// DeduplicationScope=messageGroup confines FIFO dedup to one group: the same dedup id in two
    /// groups yields two deliveries, the default queue scope yields one, and one group still dedups.
    ///
    /// Reference: https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/high-throughput-fifo.html
    /// </summary>
    [Test]
    [DisplayName("sqs.fifo-dedup-scope-message-group-allows-same-dedup-id-in-different-groups")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var token = Guid.NewGuid().ToString("N")[..8];

        // Queue A: high-throughput FIFO. Both attributes must be set AT CREATE -- setting
        // DeduplicationScope on an existing queue is legal but takes up to 60s to take effect, which
        // would make the first send race the configuration.
        var urlA = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-") + ".fifo",
            Attributes = new()
            {
                ["FifoQueue"] = "true",
                ["DeduplicationScope"] = "messageGroup",
                ["FifoThroughputLimit"] = "perMessageGroupId",
                ["VisibilityTimeout"] = "30",
            },
            Tags = new(Tags),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(urlA));

        // Queue B: plain FIFO, so DeduplicationScope defaults to `queue`.
        var urlB = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-") + ".fifo",
            Attributes = new() { ["FifoQueue"] = "true", ["VisibilityTimeout"] = "30" },
            Tags = new(Tags),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(urlB));

        var attrsA = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = urlA, AttributeNames = ["All"] })).Attributes!;
        Ensure(attrsA.GetValueOrDefault("DeduplicationScope") == "messageGroup",
            "queue A is not message-group-scoped, so nothing below distinguishes the two scopes: " +
            $"DeduplicationScope={attrsA.GetValueOrDefault("DeduplicationScope") ?? "None"}");
        Ensure(attrsA.GetValueOrDefault("FifoThroughputLimit") == "perMessageGroupId",
            "queue A is not high-throughput FIFO: " +
            $"FifoThroughputLimit={attrsA.GetValueOrDefault("FifoThroughputLimit") ?? "None"}");
        var attrsB = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = urlB, AttributeNames = ["All"] })).Attributes!;
        Ensure(attrsB.GetValueOrDefault("DeduplicationScope", "queue") == "queue",
            "the contrast queue B must carry the DEFAULT queue-wide scope, got " +
            $"{attrsB.GetValueOrDefault("DeduplicationScope") ?? "None"}");

        var (g1, g2) = ($"g1-{token}", $"g2-{token}");
        var dedupD = $"dedup-D-{token}";
        var (bodyG1, bodyG2) = ($"body-in-{g1}", $"body-in-{g2}");

        foreach (var url in new[] { urlA, urlB })
        {
            await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = bodyG1, MessageGroupId = g1, MessageDeduplicationId = dedupD });
            await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = bodyG2, MessageGroupId = g2, MessageDeduplicationId = dedupD });
        }

        var aMsgs = await Drain(sqs, urlA, timeoutSeconds: 90);
        var aBodies = aMsgs.Select(m => m.Body).Order(StringComparer.Ordinal).ToList();
        var expectedA = new[] { bodyG1, bodyG2 }.Order(StringComparer.Ordinal).ToList();
        Ensure(aBodies.SequenceEqual(expectedA),
            "DeduplicationScope=messageGroup must NOT deduplicate across groups: expected both " +
            $"bodies [{string.Join(", ", expectedA)}], got [{string.Join(", ", aBodies)}]");
        var aGroups = aMsgs.Select(m => m.Attributes!["MessageGroupId"]).ToHashSet();
        Ensure(aGroups.SetEquals([g1, g2]),
            $"the two deliveries did not come from the two distinct groups: got groups [{string.Join(", ", aGroups)}], " +
            $"expected {{{g1}, {g2}}}");

        var bMsgs = await Drain(sqs, urlB, timeoutSeconds: 90);
        var bBodies = bMsgs.Select(m => m.Body).ToList();
        Ensure(bBodies.Count == 1,
            "the default DeduplicationScope=queue must deduplicate ACROSS groups: expected exactly " +
            $"1 delivery, got {bBodies.Count} ([{string.Join(", ", bBodies)}])");
        Ensure(bBodies.SequenceEqual([bodyG1]),
            "queue-scoped deduplication must keep the FIRST send and discard the second, got " +
            $"[{string.Join(", ", bBodies)}] (expected [{bodyG1}])");

        // Control: narrowing the scope must not switch deduplication off.
        var dedupE = $"dedup-E-{token}";
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = urlA, MessageBody = $"E-first-{token}", MessageGroupId = g1, MessageDeduplicationId = dedupE });
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = urlA, MessageBody = $"E-second-{token}", MessageGroupId = g1, MessageDeduplicationId = dedupE });
        var eBodies = (await Drain(sqs, urlA, timeoutSeconds: 90)).Select(m => m.Body).ToList();
        Ensure(eBodies.SequenceEqual([$"E-first-{token}"]),
            "the same dedup id in the SAME group must still deduplicate under messageGroup scope, " +
            $"keeping the first send: got [{string.Join(", ", eBodies)}]");
    }

    /// <summary>
    /// Receive-and-delete until `empty_polls` consecutive empty polls or the deadline; -> [msg].
    ///
    /// Deleting matters: a FIFO message group is blocked while any of its messages is in flight.
    /// </summary>
    private static async Task<List<Message>> Drain(AmazonSQSClient sqs, string url, double timeoutSeconds, int waitSeconds = 5, int emptyPolls = 2)
    {
        var got = new List<Message>();
        var empties = 0;
        var deadline = Stopwatch.StartNew();
        while (empties < emptyPolls && deadline.Elapsed.TotalSeconds < timeoutSeconds)
        {
            var batch = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                // Both forms: AttributeNames is deprecated but honoured, and SQS accepts both together.
                QueueUrl = url,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = waitSeconds,
                AttributeNames = ["All"],
                MessageSystemAttributeNames = ["All"],
            })).Messages ?? [];
            if (batch.Count == 0)
            {
                empties++;
                continue;
            }

            empties = 0;
            foreach (var m in batch)
            {
                got.Add(m);
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle });
            }
        }

        return got;
    }
}

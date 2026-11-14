// Translated from behavioral/sqs/check_standard_queue_message_group_id_is_surfaced_and_never_locks_the_group.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.standard-queue-message-group-id-is-surfaced-and-never-locks-the-group</summary>
[Category("AwsEvaluationSuite")]
public sealed class StandardQueueMessageGroupIdIsSurfacedAndNeverLocksTheGroup
{
    /// <summary>
    /// A standard queue accepts MessageGroupId on SendMessage and SendMessageBatch, surfaces it on
    /// receive only for messages that carried one, assigns no SequenceNumber, and never holds a
    /// group's later messages behind an in-flight one.
    /// </summary>
    [Test]
    [DisplayName("sqs.standard-queue-message-group-id-is-surfaced-and-never-locks-the-group")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.Other, "a standard queue never surfaces MessageGroupId on receive: per body it was {'a1': None, 'a2': None, 'b1': None, 'n1': None}")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-fair-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "a1", MessageGroupId = "tenant-a" });
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "a2", MessageGroupId = "tenant-a" });
        var batch = await sqs.SendMessageBatchAsync(new SendMessageBatchRequest
        {
            QueueUrl = url,
            Entries =
            [
                new SendMessageBatchRequestEntry { Id = "b1", MessageBody = "b1", MessageGroupId = "tenant-b" },
                new SendMessageBatchRequestEntry { Id = "n1", MessageBody = "n1" },
            ],
        });
        var okIds = (batch.Successful ?? []).Select(e => e.Id).Order(StringComparer.Ordinal).ToList();
        Ensure(okIds.SequenceEqual(["b1", "n1"]),
            "SendMessageBatch with a MessageGroupId entry on a standard queue must succeed for both " +
            $"entries; Successful=[{string.Join(", ", okIds)}] Failed=[{string.Join(", ", (batch.Failed ?? []).Select(f => $"{f.Id}:{f.Code}"))}]");

        // Receive one message per call and never delete or release: every message stays in flight
        // for the whole window, so a group lock would make the second tenant-a message unreachable.
        var seen = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(40) && seen.Count < 4)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url,
                MaxNumberOfMessages = 1,
                WaitTimeSeconds = 2,
                VisibilityTimeout = 300,
                MessageSystemAttributeNames = ["MessageGroupId", "SequenceNumber"],
            });
            foreach (var m in resp.Messages ?? [])
            {
                seen[m.Body] = m.Attributes ?? [];
            }
        }

        var seenBodies = seen.Keys.Order(StringComparer.Ordinal).ToList();
        Ensure(seenBodies.SequenceEqual(["a1", "a2", "b1", "n1"]),
            "all four messages must be deliverable while each stays in flight (no group lock on a " +
            $"standard queue); received [{string.Join(", ", seenBodies)}]");
        var groups = seen.ToDictionary(kv => kv.Key, kv => kv.Value.GetValueOrDefault("MessageGroupId"), StringComparer.Ordinal);
        var wantGroups = new Dictionary<string, string?> { ["a1"] = "tenant-a", ["a2"] = "tenant-a", ["b1"] = "tenant-b", ["n1"] = null };
        Ensure(groups.Count == wantGroups.Count && groups.All(kv => wantGroups.TryGetValue(kv.Key, out var v) && v == kv.Value),
            $"MessageGroupId surfaced on receive per body was {Show(groups)}; expected each message's own " +
            "group, and none on the message sent without one");
        var seqs = seen.ToDictionary(kv => kv.Key, kv => kv.Value.GetValueOrDefault("SequenceNumber"), StringComparer.Ordinal);
        Ensure(seqs.Values.All(v => v is null),
            $"a standard queue assigns no SequenceNumber, even to grouped messages; got {Show(seqs)}");
    }

    private static string Show(Dictionary<string, string?> d)
        => "{" + string.Join(", ", d.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"'{kv.Key}': {(kv.Value is null ? "None" : $"'{kv.Value}'")}")) + "}";
}

// Translated from behavioral/sqs/check_purge_empties.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.purge-empties</summary>
[Category("AwsEvaluationSuite")]
public sealed class PurgeEmpties
{
    private static Dictionary<string, string> Tags => new() { ["sim2real:managed"] = "true", ["sim2real"] = "purge-empties" };

    /// <summary>
    /// PurgeQueue removes all messages; the sent messages are first confirmed receivable so a
    /// SendMessage that silently drops messages cannot pass.
    /// </summary>
    [Test]
    [DisplayName("sqs.purge-empties")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-"), Tags = Tags })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var sentIds = new HashSet<string>();
        for (var i = 0; i < 3; i++)
        {
            var resp = await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = $"purge-{i}" });
            sentIds.Add(resp.MessageId ?? throw new KeyNotFoundException("MessageId"));
        }

        var seenIds = new HashSet<string>();
        var allSeen = false;
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 1 });
            foreach (var msg in resp.Messages ?? [])
            {
                seenIds.Add(msg.MessageId);
                await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle, VisibilityTimeout = 0 });
            }

            if (sentIds.IsSubsetOf(seenIds))
            {
                allSeen = true;
                break;
            }
        }

        if (!allSeen)
        {
            throw Fail(
                $"expected all {sentIds.Count} sent messages to be receivable before purge, " +
                $"only observed {sentIds.Intersect(seenIds).Count()}");
        }

        await sqs.PurgeQueueAsync(new PurgeQueueRequest { QueueUrl = url });
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 1 });
            var messages = resp.Messages ?? [];
            if (messages.Count == 0)
            {
                return;
            }

            foreach (var msg in messages)
            {
                await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle, VisibilityTimeout = 0 });
            }
        }

        throw Fail("queue still returned messages after PurgeQueue");
    }
}

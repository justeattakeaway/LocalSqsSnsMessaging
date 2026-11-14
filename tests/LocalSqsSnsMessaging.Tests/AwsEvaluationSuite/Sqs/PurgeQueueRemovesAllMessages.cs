// Translated from behavioral/sqs/check_purge_queue_removes_all_messages.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.purge-queue-removes-all-messages</summary>
[Category("AwsEvaluationSuite")]
public sealed class PurgeQueueRemovesAllMessages
{
    private static Dictionary<string, string> Tags => new() { ["sim2real:managed"] = "true", ["sim2real"] = "purge-queue-removes-all-messages" };

    /// <summary>
    /// PurgeQueue removes all messages, visible and in-flight, within its documented async window.
    /// </summary>
    [Test]
    [DisplayName("sqs.purge-queue-removes-all-messages")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-"), Tags = Tags })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var sentIds = new HashSet<string>();
        for (var i = 0; i < 5; i++)
        {
            var resp = await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = $"purge-me-{i}" });
            sentIds.Add(resp.MessageId ?? throw new KeyNotFoundException("MessageId"));
        }

        // Make one message in-flight (received but not deleted) to prove purge covers in-flight too.
        var inflight = await PollUntil(
            async () => (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 5 })).Messages ?? [],
            msgs => msgs.Count == 1,
            timeoutSeconds: 20.0);
        Ensure(sentIds.Contains(inflight[0].MessageId),
            $"received message '{inflight[0].MessageId}' was not one of the sent ids [{string.Join(", ", sentIds)}]");

        // Without this independent read, an implementation whose ReceiveMessage never actually put the
        // message in flight would satisfy every assertion below, and the check would silently degrade
        // to covering only the already-visible case.
        var prePurgeNotVisible = await PollUntil(
            async () => (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = url,
                AttributeNames = ["ApproximateNumberOfMessagesNotVisible"],
            })).Attributes!["ApproximateNumberOfMessagesNotVisible"],
            v => v == "1",
            timeoutSeconds: 30.0);
        Ensure(prePurgeNotVisible == "1",
            $"the received message was never in flight, so the purge below proves nothing about " +
            $"in-flight messages: ApproximateNumberOfMessagesNotVisible='{prePurgeNotVisible}'");

        await sqs.PurgeQueueAsync(new PurgeQueueRequest { QueueUrl = url });

        // PurgeQueue is asynchronous; per docs it can take up to 60 seconds. Poll for the observable
        // condition (no sent message is ever returned again) rather than sleeping a fixed duration.
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(65.0))
        {
            var leftover = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 })).Messages ?? [];
            if (leftover.Any(m => sentIds.Contains(m.MessageId)))
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                continue;
            }

            var attrs = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = url,
                AttributeNames = ["ApproximateNumberOfMessages", "ApproximateNumberOfMessagesNotVisible"],
            })).Attributes!;
            if (attrs["ApproximateNumberOfMessages"] == "0" && attrs["ApproximateNumberOfMessagesNotVisible"] == "0")
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail("queue was not fully purged within 65s");
    }

    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutSeconds = 30.0, double intervalSeconds = 1.0)
    {
        var deadline = Stopwatch.StartNew();
        T? last = default;
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"condition not met within {timeoutSeconds}s (last={last})");
    }
}

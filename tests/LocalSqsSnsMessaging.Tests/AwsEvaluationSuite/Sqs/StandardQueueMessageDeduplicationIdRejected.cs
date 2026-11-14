// Translated from behavioral/sqs/check_standard_queue_message_deduplication_id_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.standard-queue-message-deduplication-id-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class StandardQueueMessageDeduplicationIdRejected
{
    private const string DedupId = "order-42";
    private const string DedupMessage =
        $"Value {DedupId} for parameter MessageDeduplicationId is invalid. Reason: The request " +
        "include parameter that is not valid for this queue type.";

    /// <summary>
    /// MessageDeduplicationId is refused on a standard queue, with or without MessageGroupId,
    /// and the refused sends enqueue nothing, while the same sends without it are delivered.
    /// </summary>
    [Test]
    [DisplayName("sqs.standard-queue-message-deduplication-id-rejected")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.Fifo, "expected MessageDeduplicationId on a standard queue to be refused with InvalidParameterValue, but SendMessage succeeded")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-stddedup-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        // Fair queues allow MessageGroupId on standard queues, but a standard queue never deduplicates.
        foreach (var (label, groupId) in new (string, string?)[] { ("dedup-only", null), ("dedup-with-group", "tenant-a") })
        {
            var request = new SendMessageRequest { QueueUrl = url, MessageBody = $"refused-{label}", MessageDeduplicationId = DedupId };
            if (groupId is not null)
            {
                request.MessageGroupId = groupId;
            }

            var err = await Error(() => sqs.SendMessageAsync(request));
            Ensure(err.Code == "InvalidParameterValue",
                $"{label}: MessageDeduplicationId on a standard queue gave code '{err.Code}'");
            Ensure(err.Message == DedupMessage, $"{label}: expected message '{DedupMessage}', got '{err.Message}'");
        }

        // positive controls: the same two sends without the deduplication id
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "accepted-plain" });
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "accepted-grouped", MessageGroupId = "tenant-a" });

        var bodies = new List<string>();
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            var msgs = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 })).Messages ?? [];
            if (msgs.Count == 0 && bodies.Count >= 2)
            {
                break;
            }

            foreach (var m in msgs)
            {
                bodies.Add(m.Body);
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle });
            }
        }

        var sorted = bodies.Order(StringComparer.Ordinal).ToList();
        Ensure(sorted.SequenceEqual(["accepted-grouped", "accepted-plain"]),
            "the queue must hold exactly the two accepted messages and neither refused one; " +
            $"received [{string.Join(", ", sorted)}]");
    }

    /// <summary>
    /// The botocore Error dict <paramref name="fn"/> raises. An unexpected success raises too, naming what came
    /// back: a standard queue that accepts a deduplication id must not read as a code mismatch.
    /// </summary>
    private static async Task<AwsError> Error(Func<Task<SendMessageResponse>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return error;
        }

        throw Fail($"expected the call to be refused, but it succeeded: {{'MD5OfMessageBody': '{result!.MD5OfMessageBody}', 'MessageId': '{result.MessageId}'}}");
    }
}

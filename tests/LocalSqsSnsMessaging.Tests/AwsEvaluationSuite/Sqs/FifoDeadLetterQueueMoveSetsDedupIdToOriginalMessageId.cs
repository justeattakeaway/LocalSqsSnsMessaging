// Translated from behavioral/sqs/check_fifo_dead_letter_queue_move_sets_dedup_id_to_original_message_id.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.fifo-dead-letter-queue-move-sets-dedup-id-to-original-message-id</summary>
[Category("AwsEvaluationSuite")]
public sealed class FifoDeadLetterQueueMoveSetsDedupIdToOriginalMessageId
{
    private static readonly Dictionary<string, string> Tags = new() { ["sim2real:managed"] = "true", ["sim2real"] = "fifo-dlq-dedup-id" };

    /// <summary>
    /// A message that maxReceiveCount moves from a FIFO queue to its FIFO dead-letter queue arrives
    /// there with its MessageDeduplicationId replaced by the original message's MessageId.
    ///
    /// Reference: https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/sqs-configure-dead-letter-queue-redrive.html
    /// </summary>
    [Test]
    [DisplayName("sqs.fifo-dead-letter-queue-move-sets-dedup-id-to-original-message-id")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.Fifo, "a message moved from a FIFO queue to its FIFO DLQ keeps its sent MessageDeduplicationId instead of the original MessageId")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        const string suffix = ".fifo";

        var dlqUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-") + "-dlq" + suffix,
            Attributes = new() { ["FifoQueue"] = "true" },
            Tags = new(Tags),
        })).QueueUrl;
        ctx.OnCleanup(Teardown(() => sqs.DeleteQueueAsync(dlqUrl), "AWS.SimpleQueueService.NonExistentQueue"));
        var dlqArn = await QueueArn(sqs, dlqUrl);

        var srcUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-") + "-src" + suffix,
            Attributes = new()
            {
                ["FifoQueue"] = "true",
                ["VisibilityTimeout"] = "1",
                ["RedrivePolicy"] = PyDumps(("deadLetterTargetArn", dlqArn), ("maxReceiveCount", 1)),
            },
            Tags = new(Tags),
        })).QueueUrl;
        ctx.OnCleanup(Teardown(() => sqs.DeleteQueueAsync(srcUrl), "AWS.SimpleQueueService.NonExistentQueue"));

        var token = Guid.NewGuid().ToString("N")[..8];
        var body = $"fifo-dlq-dedup-{token}";
        var dedupId = $"dedup-{token}";
        var sentId = (await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = srcUrl,
            MessageBody = body,
            MessageGroupId = FidelityContext.UniqueName("g-"),
            MessageDeduplicationId = dedupId,
        })).MessageId;

        // On the source the message carries the deduplication id it was sent with.
        var first = await ReceiveFromSource(sqs, srcUrl, sentId, timeoutSeconds: BudgetSeconds(ctx, 30));
        Ensure(first.Attributes!.GetValueOrDefault("MessageDeduplicationId") == dedupId,
            "the source delivered MessageDeduplicationId " +
            $"{first.Attributes.GetValueOrDefault("MessageDeduplicationId") ?? "None"}, expected the sent {dedupId}");
        // Not deleted: the 1 s VisibilityTimeout lapses, so the next receive exceeds maxReceiveCount=1.

        var dlqMsg = await AwaitDlqArrival(sqs, srcUrl, dlqUrl, token: token, timeoutSeconds: BudgetSeconds(ctx, 40));
        Ensure(dlqMsg.Body == body, $"DLQ copy carried {dlqMsg.Body}, expected {body}");
        var dlqDedupId = dlqMsg.Attributes!.GetValueOrDefault("MessageDeduplicationId");
        Ensure(dlqDedupId == sentId,
            "a message moved from a FIFO queue to its FIFO DLQ must carry the original MessageId " +
            $"{sentId} as its MessageDeduplicationId, got {dlqDedupId ?? "None"}");
    }

    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound)
        => async () =>
        {
            var error = await FidelityContext.CaptureError(undo);
            if (error is not null && !notFound.Contains(error.Code))
            {
                throw error.Exception;
            }
        };

    private static async Task<string> QueueArn(AmazonSQSClient sqs, string url)
        => (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];

    /// <summary>A wait's timeout, bounded by the runner's remaining time so the wait fails with its own message.</summary>
    private static double BudgetSeconds(FidelityContext ctx, double capSeconds)
    {
        var left = ctx.TimeLeftSeconds;
        return Math.Max(1.0, Math.Min(capSeconds, left - 5.0));
    }

    /// <summary>Receive until the source delivers `message_id`; return it with its system attributes.</summary>
    private static async Task<Message> ReceiveFromSource(AmazonSQSClient sqs, string srcUrl, string messageId, double timeoutSeconds)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed.TotalSeconds < timeoutSeconds)
        {
            foreach (var msg in (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
                     {
                         QueueUrl = srcUrl,
                         MaxNumberOfMessages = 1,
                         WaitTimeSeconds = 2,
                         MessageSystemAttributeNames = ["All"],
                     })).Messages ?? [])
            {
                if (msg.MessageId == messageId)
                {
                    return msg;
                }
            }
        }

        throw new TimeoutException($"the source never delivered {messageId} within {timeoutSeconds:F0}s");
    }

    /// <summary>Drive the source's redelivery past maxReceiveCount and return the DLQ copy with its attributes.</summary>
    private static async Task<Message> AwaitDlqArrival(AmazonSQSClient sqs, string srcUrl, string dlqUrl, string token, double timeoutSeconds)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed.TotalSeconds < timeoutSeconds)
        {
            foreach (var msg in (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = srcUrl, MaxNumberOfMessages = 1, WaitTimeSeconds = 1 })).Messages ?? [])
            {
                await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest { QueueUrl = srcUrl, ReceiptHandle = msg.ReceiptHandle, VisibilityTimeout = 0 });
            }

            foreach (var msg in (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
                     {
                         QueueUrl = dlqUrl,
                         MaxNumberOfMessages = 1,
                         WaitTimeSeconds = 1,
                         MessageSystemAttributeNames = ["All"],
                     })).Messages ?? [])
            {
                if (msg.Body.Contains(token, StringComparison.Ordinal))
                {
                    return msg;
                }
            }
        }

        throw new TimeoutException($"{token} never reached the DLQ within {timeoutSeconds:F0}s");
    }

    /// <summary>Python's <c>json.dumps</c> of a flat dict, with its default separators.</summary>
    private static string PyDumps(params (string Key, object Value)[] items)
        => "{" + string.Join(", ", items.Select(kv => $"{JsonSerializer.Serialize(kv.Key)}: {JsonSerializer.Serialize(kv.Value)}")) + "}";
}

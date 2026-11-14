// Translated from behavioral/sqs/check_message_move_task_completes_and_drains_dlq.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using System.Globalization;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.message-move-task-completes-and-drains-dlq</summary>
[Category("AwsEvaluationSuite")]
public sealed class MessageMoveTaskCompletesAndDrainsDlq
{
    private static readonly string[] Terminal = ["COMPLETED", "FAILED", "CANCELLED"];

    private static Dictionary<string, string> Tags => new()
    {
        ["sim2real:managed"] = "true",
        ["sim2real"] = "message-move-task-completes-and-drains-dlq",
    };

    private static async Task<T> Poll<T>(Func<Task<T>> fn, Func<T, bool> pred, double timeoutSeconds, double intervalSeconds = 1.0)
    {
        var deadline = Stopwatch.StartNew();
        var val = await fn();
        while (!pred(val))
        {
            if (deadline.Elapsed >= TimeSpan.FromSeconds(timeoutSeconds))
            {
                return val;
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds));
            val = await fn();
        }

        return val;
    }

    /// <summary>
    /// Collect distinct bodies WITHOUT consuming them: VisibilityTimeout=0 keeps each message
    /// immediately visible again, so the queue is left as it was found.
    /// </summary>
    private static async Task<List<string>> Peek(AmazonSQSClient sqs, string url, int expect, double timeoutSeconds)
    {
        var seen = new Dictionary<string, string>();
        var deadline = Stopwatch.StartNew();
        while (seen.Count < expect && deadline.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 1 });
            foreach (var msg in resp.Messages ?? [])
            {
                seen[msg.MessageId] = msg.Body;
                await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle, VisibilityTimeout = 0 });
            }
        }

        return [.. seen.Values.Order(StringComparer.Ordinal)];
    }

    /// <summary>Receive-and-delete distinct messages, returning their bodies sorted.</summary>
    private static async Task<List<string>> Drain(AmazonSQSClient sqs, string url, int expect, double timeoutSeconds)
    {
        var seen = new Dictionary<string, string>();
        var deadline = Stopwatch.StartNew();
        while (seen.Count < expect && deadline.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 1 });
            foreach (var msg in resp.Messages ?? [])
            {
                seen[msg.MessageId] = msg.Body;
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle });
            }
        }

        return [.. seen.Values.Order(StringComparer.Ordinal)];
    }

    private static string Show(IEnumerable<string> values) => "[" + string.Join(", ", values.Select(v => $"'{v}'")) + "]";

    private static string Show(ListMessageMoveTasksResultEntry? t)
        => t is null
            ? "{}"
            : $"{{TaskHandle: {t.TaskHandle}, Status: {t.Status}, SourceArn: {t.SourceArn}, DestinationArn: {t.DestinationArn}, " +
              $"ApproximateNumberOfMessagesMoved: {t.ApproximateNumberOfMessagesMoved}, ApproximateNumberOfMessagesToMove: {t.ApproximateNumberOfMessagesToMove}, FailureReason: {t.FailureReason}}}";

    /// <summary>
    /// A message move task from a DLQ reaches Status=COMPLETED exactly, reports the messages moved
    /// and to move, redelivers them on the destination queue, and leaves the DLQ empty.
    ///
    /// Reference: https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_StartMessageMoveTask.html
    /// </summary>
    [Test]
    [DisplayName("sqs.message-move-task-completes-and-drains-dlq")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.MoveTasks, "Check timed out after 60.0s")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;

        // Created first so LIFO teardown deletes the dead-letter target last.
        var dlqUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-") + "-dlq",
            Tags = Tags,
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(dlqUrl));
        var dlqArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = dlqUrl, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];

        var srcUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-") + "-src",
            Attributes = new()
            {
                ["VisibilityTimeout"] = "1",
                ["RedrivePolicy"] = $"{{\"deadLetterTargetArn\":\"{dlqArn}\",\"maxReceiveCount\":1}}",
            },
            Tags = Tags,
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(srcUrl));
        var srcArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = srcUrl, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];

        var token = Guid.NewGuid().ToString("N")[..8];
        var bodies = Enumerable.Range(0, 2).Select(i => $"move-{i}-{token}").Order(StringComparer.Ordinal).ToList();
        foreach (var body in bodies)
        {
            await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = srcUrl, MessageBody = body });
        }

        // --- land both messages in the DLQ ------------------------------------------------
        // Redrive fires on the receive that would take a message PAST maxReceiveCount, so one delivery
        // each is not enough; re-receive (VisibilityTimeout=0 re-exposes each) until the DLQ fills.
        var delivered = new Dictionary<string, int>();
        var deadline = Stopwatch.StartNew();
        var dlqDepth = "0";
        while (deadline.Elapsed < TimeSpan.FromSeconds(150))
        {
            foreach (var msg in (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = srcUrl,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 1,
                AttributeNames = ["ApproximateReceiveCount"],
            })).Messages ?? [])
            {
                delivered[msg.Body] = delivered.GetValueOrDefault(msg.Body) + 1;
                await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest { QueueUrl = srcUrl, ReceiptHandle = msg.ReceiptHandle, VisibilityTimeout = 0 });
            }

            dlqDepth = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = dlqUrl,
                AttributeNames = ["ApproximateNumberOfMessages"],
            })).Attributes!.GetValueOrDefault("ApproximateNumberOfMessages", "0");
            if (int.Parse(dlqDepth, CultureInfo.InvariantCulture) >= bodies.Count)
            {
                break;
            }
        }

        Ensure(delivered.Keys.ToHashSet().SetEquals(bodies),
            $"could not deliver both messages on the source queue: got {Show(delivered.Keys.Order(StringComparer.Ordinal))}");
        // The source must have given each message up rather than kept redelivering it.
        var srcDepth = await Poll(
            async () => (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = srcUrl,
                AttributeNames = ["ApproximateNumberOfMessages"],
            })).Attributes!.GetValueOrDefault("ApproximateNumberOfMessages", "?"),
            v => v == "0",
            timeoutSeconds: 60);
        Ensure(srcDepth == "0",
            $"source queue still reports {srcDepth} message(s) after redrive; the messages were " +
            $"not moved off it (deliveries per message: {string.Join(", ", delivered.Select(kv => $"{kv.Key}: {kv.Value}"))})");

        var inDlq = await Peek(sqs, dlqUrl, expect: 2, timeoutSeconds: 90);
        Ensure(inDlq.SequenceEqual(bodies), $"redrive did not land both messages in the DLQ: {Show(inDlq)} != {Show(bodies)}");

        // --- start the move task and poll it to a TERMINAL status --------------------------
        var started = await sqs.StartMessageMoveTaskAsync(new StartMessageMoveTaskRequest { SourceArn = dlqArn, DestinationArn = srcArn });
        var handle = started.TaskHandle;
        Ensure(!string.IsNullOrEmpty(handle), "StartMessageMoveTask returned no TaskHandle");

        async Task<ListMessageMoveTasksResultEntry?> TaskEntry()
        {
            foreach (var res in (await sqs.ListMessageMoveTasksAsync(new ListMessageMoveTasksRequest { SourceArn = dlqArn, MaxResults = 10 })).Results ?? [])
            {
                if (res.TaskHandle == handle || res.SourceArn == dlqArn)
                {
                    return res;
                }
            }

            return null;
        }

        var task = await Poll(TaskEntry, t => Terminal.Contains(t?.Status), timeoutSeconds: 180);
        Ensure(task is not null, $"ListMessageMoveTasks reported no task for source {dlqArn}");
        Ensure(task.Status == "COMPLETED",
            $"move task ended in Status='{task.Status}' (FailureReason='{task.FailureReason}'), expected COMPLETED");
        Ensure(task.DestinationArn == srcArn, $"task DestinationArn '{task.DestinationArn}' != '{srcArn}'");
        // ApproximateNumberOfMessagesMoved lags the COMPLETED status by seconds, so poll for it. A
        // COMPLETED entry drops TaskHandle, which is why _task() also matches on SourceArn.
        task = await Poll(TaskEntry, t => (t?.ApproximateNumberOfMessagesMoved ?? 0) >= bodies.Count, timeoutSeconds: 120, intervalSeconds: 3.0);
        var moved = task?.ApproximateNumberOfMessagesMoved;
        Ensure(moved is long m && m >= bodies.Count,
            $"ApproximateNumberOfMessagesMoved={moved} for a {bodies.Count}-message task -- a " +
            "COMPLETED task that moved nothing is the stub this check exists to catch");
        // A COMPLETED task also publishes what it set out to move; anything else means the task
        // never sized the DLQ it drained.
        var toMove = task?.ApproximateNumberOfMessagesToMove;
        Ensure(toMove == bodies.Count,
            $"a COMPLETED task over a {bodies.Count}-message DLQ must report " +
            $"ApproximateNumberOfMessagesToMove={bodies.Count}, got {toMove} ({Show(task)})");

        // --- the messages really are back on the destination ------------------------------
        var back = await Drain(sqs, srcUrl, expect: 2, timeoutSeconds: 90);
        Ensure(back.SequenceEqual(bodies), $"destination queue received {Show(back)}, expected {Show(bodies)}");

        // --- effect on the source of the move: the DLQ is drained -------------------------
        dlqDepth = await Poll(
            async () => (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = dlqUrl,
                AttributeNames = ["ApproximateNumberOfMessages"],
            })).Attributes!.GetValueOrDefault("ApproximateNumberOfMessages", "?"),
            v => v == "0",
            timeoutSeconds: 60);
        Ensure(dlqDepth == "0", $"DLQ still reports {dlqDepth} message(s) after a COMPLETED move task");
        var left = await Peek(sqs, dlqUrl, expect: 1, timeoutSeconds: 10);
        Ensure(left.Count == 0, $"DLQ still delivers {Show(left)} after a COMPLETED move task");
    }
}

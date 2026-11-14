// Translated from behavioral/sqs/check_message_move_task_redrive_assigns_new_message_id_and_sent_timestamp.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.message-move-task-redrive-assigns-new-message-id-and-sent-timestamp</summary>
[Category("AwsEvaluationSuite")]
public sealed class MessageMoveTaskRedriveAssignsNewMessageIdAndSentTimestamp
{
    private static readonly string[] TerminalStatuses = ["COMPLETED", "FAILED", "CANCELLED"];

    private static Dictionary<string, string> Tags => new()
    {
        ["sim2real:managed"] = "true",
        ["sim2real"] = "mmt-new-id-and-timestamp",
    };

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

    /// <summary>Poll fn() until pred holds; a timeout, capped by the runner's remaining time, raises.</summary>
    private static async Task<T> Poll<T>(FidelityContext ctx, Func<Task<T>> fn, Func<T, bool> pred, double timeoutSeconds, double intervalSeconds = 2.0)
    {
        var budget = BudgetSeconds(ctx, timeoutSeconds);
        var deadline = Stopwatch.StartNew();
        var val = await fn();
        while (!pred(val))
        {
            if (deadline.Elapsed >= TimeSpan.FromSeconds(budget))
            {
                throw new TimeoutException($"condition not met within {budget:F0}s; last value {Describe(val)}");
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds));
            val = await fn();
        }

        return val;
    }

    private static string Describe<T>(T val) => val switch
    {
        null => "{}",
        ListMessageMoveTasksResultEntry t => $"{{TaskHandle: {t.TaskHandle}, Status: {t.Status}, SourceArn: {t.SourceArn}, DestinationArn: {t.DestinationArn}, FailureReason: {t.FailureReason}}}",
        ReceiveMessageResponse r => $"{{Messages: {r.Messages?.Count.ToString() ?? "None"}}}",
        _ => val.ToString() ?? "",
    };

    /// <summary>Drive the source's redelivery past maxReceiveCount and return the DLQ copy with its attributes.</summary>
    private static async Task<Message> AwaitDlqArrival(AmazonSQSClient sqs, string srcUrl, string dlqUrl, string token, double timeoutSeconds)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
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

        throw new TimeoutException($"'{token}' never reached the DLQ within {timeoutSeconds:F0}s");
    }

    /// <summary>
    /// A message that StartMessageMoveTask redrives from a DLQ back to its source arrives there
    /// with a MessageId and a SentTimestamp distinct from the copy that sat in the DLQ.
    /// </summary>
    [Test]
    [DisplayName("sqs.message-move-task-redrive-assigns-new-message-id-and-sent-timestamp")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.MoveTasks, "move task never reaches a terminal status: condition not met within 54s; last value Status=RUNNING")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;

        var dlqUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-") + "-dlq", Tags = Tags })).QueueUrl;
        ctx.OnCleanup(Teardown(() => sqs.DeleteQueueAsync(dlqUrl), "AWS.SimpleQueueService.NonExistentQueue"));
        var dlqArn = await QueueArn(sqs, dlqUrl);

        var srcUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-") + "-src",
            Attributes = new()
            {
                ["VisibilityTimeout"] = "1",
                ["RedrivePolicy"] = $"{{\"deadLetterTargetArn\": \"{dlqArn}\", \"maxReceiveCount\": 1}}",
            },
            Tags = Tags,
        })).QueueUrl;
        ctx.OnCleanup(Teardown(() => sqs.DeleteQueueAsync(srcUrl), "AWS.SimpleQueueService.NonExistentQueue"));
        var srcArn = await QueueArn(sqs, srcUrl);

        var token = Guid.NewGuid().ToString("N")[..8];
        var body = $"mmt-id-{token}";
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = srcUrl, MessageBody = body });

        var first = await Poll(
            ctx,
            () => sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = srcUrl, MaxNumberOfMessages = 1, WaitTimeSeconds = 5 }),
            r => r.Messages is { Count: > 0 },
            timeoutSeconds: 30);
        Ensure(first.Messages is { Count: > 0 }, "expected the first receive on the source to return the message");
        // don't delete: let the 1s VisibilityTimeout lapse so the next receive exceeds maxReceiveCount=1

        var dlqMsg = await AwaitDlqArrival(sqs, srcUrl, dlqUrl, token, timeoutSeconds: BudgetSeconds(ctx, 40));
        Ensure(dlqMsg.Body == body, $"DLQ copy carried '{dlqMsg.Body}', expected '{body}'");
        var dlqMessageId = dlqMsg.MessageId;
        var dlqSentTs = dlqMsg.Attributes!["SentTimestamp"];
        // the receive above left it in-flight for the DLQ's default VisibilityTimeout; release it so
        // StartMessageMoveTask finds it present and visible.
        await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest { QueueUrl = dlqUrl, ReceiptHandle = dlqMsg.ReceiptHandle, VisibilityTimeout = 0 });

        var handle = (await sqs.StartMessageMoveTaskAsync(new StartMessageMoveTaskRequest { SourceArn = dlqArn, DestinationArn = srcArn })).TaskHandle;
        Ensure(!string.IsNullOrEmpty(handle), "StartMessageMoveTask returned no TaskHandle");

        async Task<ListMessageMoveTasksResultEntry?> TaskEntry()
        {
            var results = (await sqs.ListMessageMoveTasksAsync(new ListMessageMoveTasksRequest { SourceArn = dlqArn, MaxResults = 1 })).Results ?? [];
            return results.Count > 0 ? results[0] : null;
        }

        var task = await Poll(ctx, TaskEntry, t => TerminalStatuses.Contains(t?.Status), timeoutSeconds: 90, intervalSeconds: 2.0);
        Ensure(task?.Status == "COMPLETED",
            $"move task ended in Status='{task?.Status}' (FailureReason='{task?.FailureReason}'), expected COMPLETED");

        var moved = await Poll(
            ctx,
            () => sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = srcUrl,
                MaxNumberOfMessages = 1,
                WaitTimeSeconds = 5,
                MessageSystemAttributeNames = ["All"],
            }),
            r => r.Messages is { Count: > 0 },
            timeoutSeconds: 60);
        Ensure(moved.Messages is { Count: > 0 }, "redriven message never reappeared on the source queue");
        var movedMsg = moved.Messages[0];
        Ensure(movedMsg.Body == body, $"redelivered message carried '{movedMsg.Body}', expected '{body}'");
        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = srcUrl, ReceiptHandle = movedMsg.ReceiptHandle });

        Ensure(movedMsg.MessageId != dlqMessageId,
            $"the redriven message kept the DLQ copy's MessageId '{dlqMessageId}' instead of being assigned a new one");
        var movedSentTs = movedMsg.Attributes!["SentTimestamp"];
        Ensure(movedSentTs != dlqSentTs,
            $"the redriven message kept the DLQ copy's SentTimestamp '{dlqSentTs}' instead of " +
            $"being assigned a new one ('{movedSentTs}')");
    }
}

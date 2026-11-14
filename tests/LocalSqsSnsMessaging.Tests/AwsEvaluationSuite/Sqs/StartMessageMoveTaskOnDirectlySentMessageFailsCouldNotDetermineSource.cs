// Translated from behavioral/sqs/check_start_message_move_task_on_directly_sent_message_fails_could_not_determine_source.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.start-message-move-task-on-directly-sent-message-fails-could-not-determine-source</summary>
[Category("AwsEvaluationSuite")]
public sealed class StartMessageMoveTaskOnDirectlySentMessageFailsCouldNotDetermineSource
{
    private static readonly HashSet<string> Terminal = new(StringComparer.Ordinal) { "COMPLETED", "FAILED", "CANCELLED" };
    private const string FailureReason = "CouldNotDetermineMessageSource";

    private static Dictionary<string, string> Tags() => new() { ["sim2real:managed"] = "true", ["sim2real"] = "mmt-cndms" };

    /// <summary>
    /// StartMessageMoveTask with no DestinationArn on a message sent straight to the DLQ starts,
    /// then settles FAILED with FailureReason CouldNotDetermineMessageSource, leaving the message on
    /// the DLQ; an explicit DestinationArn instead completes and delivers it there.
    ///
    /// Reference: https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_StartMessageMoveTask.html
    /// </summary>
    [Test]
    [DisplayName("sqs.start-message-move-task-on-directly-sent-message-fails-could-not-determine-source")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var token = Guid.NewGuid().ToString("N")[..8];

        var dlqUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-") + "-dlq", Tags = Tags() })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(dlqUrl));
        var dlqArn = await QueueArn(sqs, dlqUrl);

        // Wires dlq_url as a real DLQ (StartMessageMoveTask refuses a SourceArn that is not one),
        // without ever redriving a message through it.
        var ownerUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-") + "-owner",
            Attributes = new() { ["RedrivePolicy"] = new JsonObject { ["deadLetterTargetArn"] = dlqArn, ["maxReceiveCount"] = 5 }.ToJsonString() },
            Tags = Tags(),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(ownerUrl));

        var thirdUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-") + "-third", Tags = Tags() })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(thirdUrl));
        var thirdArn = await QueueArn(sqs, thirdUrl);

        var body = $"direct-to-dlq-{token}";
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = dlqUrl, MessageBody = body });
        var present = await Poll(
            ctx,
            async () => (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = dlqUrl, AttributeNames = ["ApproximateNumberOfMessages"] })).Attributes!["ApproximateNumberOfMessages"],
            v => v == "1",
            timeoutSeconds: 30,
            describe: v => $"'{v}'");
        Ensure(present == "1",
            "precondition: the directly-sent message must be visible on the DLQ before the move " +
            $"task starts, got ApproximateNumberOfMessages='{present}'");

        // Break: no DestinationArn, so AWS must infer the source from the message's redrive history,
        // and this message has none -- it was sent to the DLQ directly.
        var handle = (await sqs.StartMessageMoveTaskAsync(new StartMessageMoveTaskRequest { SourceArn = dlqArn })).TaskHandle;
        Ensure(!string.IsNullOrEmpty(handle), "StartMessageMoveTask returned no TaskHandle");

        var failed = await Poll(ctx, () => TaskEntry(sqs, dlqArn), t => t?.Status is { } s && Terminal.Contains(s),
            timeoutSeconds: 90, intervalSeconds: 2.0, describe: Describe);
        Ensure(failed?.Status == "FAILED",
            "a move task with no DestinationArn over a directly-sent message must settle FAILED, " +
            $"got Status='{failed?.Status}'");
        Ensure(failed!.FailureReason == FailureReason,
            $"expected FailureReason '{FailureReason}', got '{failed.FailureReason}'");

        // Effect absent: the message was never moved anywhere. ApproximateNumberOfMessages is
        // eventually consistent, so confirm presence by actually receiving it instead.
        var stillThere = await Poll(
            ctx,
            async () => (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = dlqUrl, MaxNumberOfMessages = 1, WaitTimeSeconds = 2, VisibilityTimeout = 5 })).Messages ?? [],
            msgs => msgs.Count > 0,
            timeoutSeconds: 30,
            intervalSeconds: 2.0,
            describe: msgs => $"[{string.Join(", ", msgs.Select(m => m.Body))}]");
        Ensure(stillThere.Count > 0 && stillThere[0].Body == body,
            $"a FAILED move task must leave '{body}' receivable on the DLQ, got [{string.Join(", ", stillThere.Select(m => m.Body))}]");
        await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest { QueueUrl = dlqUrl, ReceiptHandle = stillThere[0].ReceiptHandle, VisibilityTimeout = 0 });

        // Positive control: the identical source, with an explicit DestinationArn, needs no inferred
        // source and completes, delivering the message to that destination.
        var handle2 = (await sqs.StartMessageMoveTaskAsync(new StartMessageMoveTaskRequest { SourceArn = dlqArn, DestinationArn = thirdArn })).TaskHandle;
        Ensure(!string.IsNullOrEmpty(handle2), "StartMessageMoveTask with an explicit DestinationArn returned no TaskHandle");

        // The listing can still show the FAILED task first, so wait for the newer task to settle.
        var failedStarted = failed!.StartedTimestamp ?? throw new KeyNotFoundException("'StartedTimestamp'");
        var completed = await Poll(
            ctx,
            () => TaskEntry(sqs, dlqArn),
            t => (t?.StartedTimestamp ?? 0) > failedStarted && t?.Status is { } s && Terminal.Contains(s),
            timeoutSeconds: 90,
            intervalSeconds: 2.0,
            describe: Describe);
        Ensure(completed?.Status == "COMPLETED",
            "positive control: an explicit DestinationArn must complete, got " +
            $"Status='{completed?.Status}' (FailureReason='{completed?.FailureReason}')");

        var delivered = await Poll(
            ctx,
            async () => (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = thirdUrl, MaxNumberOfMessages = 1, WaitTimeSeconds = 2 })).Messages ?? [],
            msgs => msgs.Count > 0,
            timeoutSeconds: 30,
            intervalSeconds: 0.5,
            describe: msgs => $"[{string.Join(", ", msgs.Select(m => m.Body))}]");
        Ensure(delivered.Count == 1 && delivered[0].Body == body,
            $"positive control: expected '{body}' delivered to the explicit destination, got " +
            $"[{string.Join(", ", delivered.Select(m => m.Body))}]");
        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = thirdUrl, ReceiptHandle = delivered[0].ReceiptHandle });
    }

    private static async Task<string> QueueArn(AmazonSQSClient sqs, string url)
        => (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];

    private static async Task<ListMessageMoveTasksResultEntry?> TaskEntry(AmazonSQSClient sqs, string sourceArn)
    {
        var results = (await sqs.ListMessageMoveTasksAsync(new ListMessageMoveTasksRequest { SourceArn = sourceArn, MaxResults = 1 })).Results ?? [];
        return results.Count > 0 ? results[0] : null;
    }

    private static string Describe(ListMessageMoveTasksResultEntry? t)
        => t is null
            ? "{}"
            : $"{{'TaskHandle': '{t.TaskHandle}', 'Status': '{t.Status}', 'SourceArn': '{t.SourceArn}', 'DestinationArn': '{t.DestinationArn}', " +
              $"'StartedTimestamp': {t.StartedTimestamp}, 'FailureReason': '{t.FailureReason}'}}";

    /// <summary>Poll fn() until pred holds; a timeout, capped by the runner's remaining time, raises.</summary>
    private static async Task<T> Poll<T>(FidelityContext ctx, Func<Task<T>> fn, Func<T, bool> pred, double timeoutSeconds, Func<T, string> describe, double intervalSeconds = 2.0)
    {
        var budget = BudgetSeconds(ctx, timeoutSeconds);
        var deadline = FidelityTime.StartNew();
        var val = await fn();
        while (!pred(val))
        {
            if (deadline.Elapsed >= TimeSpan.FromSeconds(budget))
            {
                throw new TimeoutException($"condition not met within {budget:F0}s; last value {describe(val)}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
            val = await fn();
        }

        return val;
    }

    /// <summary>A wait's timeout, bounded by the runner's remaining time so the wait fails with its own message.</summary>
    private static double BudgetSeconds(FidelityContext ctx, double capSeconds)
        => Math.Max(1.0, Math.Min(capSeconds, ctx.TimeLeftSeconds - 5.0));
}

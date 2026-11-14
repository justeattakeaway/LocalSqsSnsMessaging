// Translated from behavioral/sqs/check_message_move_task_redrives_dlq_back_to_source.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.message-move-task-redrives-dlq-back-to-source</summary>
[Category("AwsEvaluationSuite")]
public sealed class MessageMoveTaskRedrivesDlqBackToSource
{
    /// <summary>
    /// StartMessageMoveTask on a DLQ moves its messages back to their original source
    /// queue and the task's Status transitions RUNNING -> COMPLETED.
    /// </summary>
    [Test]
    [DisplayName("sqs.message-move-task-redrives-dlq-back-to-source")]
    // The move task has to wait out the DLQ copy's 30s visibility timeout, so this check spends ~53s
    // of the runner's 60s budget; under a fully parallel test run that headroom isn't enough. It gets
    // the real-time budget instead; what it asserts is unchanged.
    [Timeout(FidelityContext.RealTimeBudgetMilliseconds)]
    [Category(ApplyDefaultCategoryAttribute.TimeBasedCategoryName)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.RealTimeBudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var dlqUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-sqs-mmt-dlq-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(dlqUrl));
        var dlqArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = dlqUrl, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];

        var redrivePolicy = $"{{\"deadLetterTargetArn\": \"{dlqArn}\", \"maxReceiveCount\": 1}}";
        var srcUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-sqs-mmt-src-"),
            Attributes = new() { ["VisibilityTimeout"] = "2", ["RedrivePolicy"] = redrivePolicy },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(srcUrl));

        const string body = "move-task-roundtrip";
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = srcUrl, MessageBody = body });

        // drive it into the DLQ via a real redrive (receive once, let it expire, receive again)
        var first = await PollReceiveOne(sqs, srcUrl, timeoutSeconds: 20.0);
        Ensure(first.Body == body, $"expected '{body}' on first receive, got '{first.Body}'");

        // wait past the 2s visibility timeout via a bounded retry (never a bare sleep), then
        // actively attempt redelivery from the source queue so SQS evaluates maxReceiveCount
        // (1) and routes the message to the DLQ. Each call's WaitTimeSeconds=2 long-polls, so
        // the retry loop itself carries the wait.
        var redeliverDeadline = Stopwatch.StartNew();
        while (redeliverDeadline.Elapsed < TimeSpan.FromSeconds(20.0))
        {
            await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = srcUrl, MaxNumberOfMessages = 1, WaitTimeSeconds = 2 });
        }

        var dlqMsg = await PollReceiveOne(sqs, dlqUrl, timeoutSeconds: 90.0);
        Ensure(dlqMsg.Body == body, $"expected '{body}' in the DLQ after redrive, got '{dlqMsg.Body}'");
        // leave it un-deleted in the DLQ so the move task has something to move

        _ = (await sqs.StartMessageMoveTaskAsync(new StartMessageMoveTaskRequest { SourceArn = dlqArn })).TaskHandle
            ?? throw new KeyNotFoundException("TaskHandle");

        // Only one active message-move task is supported per queue at a time, and
        // ListMessageMoveTasks returns the most recent task first; TaskHandle may be
        // dropped from the entry once the task is no longer active, so match on
        // SourceArn/recency instead of trying to match the handle by identity.
        async Task<string?> TaskStatus()
        {
            var results = (await sqs.ListMessageMoveTasksAsync(new ListMessageMoveTasksRequest { SourceArn = dlqArn, MaxResults = 1 })).Results
                ?? throw new KeyNotFoundException("Results");
            if (results.Count == 0)
            {
                return null;
            }

            return results[0].Status;
        }

        await PollUntil(TaskStatus, s => s == "COMPLETED", timeoutSeconds: 90.0, intervalSeconds: 3.0);

        // the message must now be back on the source queue
        var movedBack = await PollReceiveOne(sqs, srcUrl, timeoutSeconds: 30.0);
        Ensure(movedBack.Body == body, $"expected '{body}' back on the source queue after COMPLETED, got '{movedBack.Body}'");
        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = srcUrl, ReceiptHandle = movedBack.ReceiptHandle });

        // the DLQ must have given the message up, not merely copied it
        async Task<string?> DlqDepth()
            => (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = dlqUrl,
                AttributeNames = ["ApproximateNumberOfMessages"],
            })).Attributes!.GetValueOrDefault("ApproximateNumberOfMessages");

        var dlqDepth = await PollUntil(DlqDepth, v => v == "0", timeoutSeconds: 30.0, intervalSeconds: 2.0);
        Ensure(dlqDepth == "0", $"expected DLQ depth 0 after the move, got '{dlqDepth}'");
    }

    private static async Task<Message> PollReceiveOne(AmazonSQSClient sqs, string url, double timeoutSeconds = 20.0)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 3 });
            if (resp.Messages is { Count: > 0 } messages)
            {
                return messages[0];
            }
        }

        throw Fail($"no message received from {url} within {timeoutSeconds}s");
    }

    private static async Task<T?> PollUntil<T>(Func<Task<T?>> probe, Func<T?, bool> predicate, double timeoutSeconds = 30.0, double intervalSeconds = 1.0)
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

        throw Fail($"condition not met within {timeoutSeconds}s (last='{last}')");
    }
}

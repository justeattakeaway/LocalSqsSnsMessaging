// Translated from behavioral/sqs/check_start_message_move_task_destination_arn_to_third_queue_bypasses_original_source.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.start-message-move-task-destination-arn-to-third-queue-bypasses-original-source</summary>
[Category("AwsEvaluationSuite")]
public sealed class StartMessageMoveTaskDestinationArnToThirdQueueBypassesOriginalSource
{
    private static readonly HashSet<string> Terminal = new(StringComparer.Ordinal) { "COMPLETED", "FAILED", "CANCELLED" };

    private static Dictionary<string, string> Tags() => new() { ["sim2real:managed"] = "true", ["sim2real"] = "mmt-third-queue-destination" };

    /// <summary>
    /// StartMessageMoveTask with an explicit DestinationArn moves a DLQ's messages to that third
    /// queue instead of the original source, which is never touched.
    /// </summary>
    [Test]
    [DisplayName("sqs.start-message-move-task-destination-arn-to-third-queue-bypasses-original-source")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var token = Guid.NewGuid().ToString("N")[..8];

        var dlqUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-dlq-"), Tags = Tags() })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(dlqUrl));
        var dlqArn = await QueueArn(sqs, dlqUrl);

        var srcUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-src-"),
            Attributes = new() { ["RedrivePolicy"] = new JsonObject { ["deadLetterTargetArn"] = dlqArn, ["maxReceiveCount"] = 1 }.ToJsonString() },
            Tags = Tags(),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(srcUrl));

        var thirdUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-third-"), Tags = Tags() })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(thirdUrl));
        var thirdArn = await QueueArn(sqs, thirdUrl);

        var body = $"{token}-moved";
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = srcUrl, MessageBody = body });

        async Task<string> LandedInDlq()
        {
            for (var i = 0; i < 8; i++)
            {
                var r = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = srcUrl, MaxNumberOfMessages = 1, WaitTimeSeconds = 1, VisibilityTimeout = 1 });
                if (r.Messages is null or { Count: 0 })
                {
                    break;
                }

                await FidelityTime.Delay(TimeSpan.FromSeconds(2));
            }

            return (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = dlqUrl,
                AttributeNames = ["ApproximateNumberOfMessages"],
            })).Attributes!["ApproximateNumberOfMessages"];
        }

        var landedCount = await Poll(LandedInDlq, v => v == "1", timeoutSeconds: 30, intervalSeconds: 3);
        Ensure(landedCount == "1",
            $"precondition: '{body}' must have redriven into the DLQ before the move task is " +
            $"started; the DLQ's ApproximateNumberOfMessages read '{landedCount}'");

        var handle = (await sqs.StartMessageMoveTaskAsync(new StartMessageMoveTaskRequest { SourceArn = dlqArn, DestinationArn = thirdArn })).TaskHandle;
        Ensure(!string.IsNullOrEmpty(handle), "StartMessageMoveTask returned no TaskHandle");

        async Task<ListMessageMoveTasksResultEntry?> TaskEntry()
        {
            var results = (await sqs.ListMessageMoveTasksAsync(new ListMessageMoveTasksRequest { SourceArn = dlqArn, MaxResults = 1 })).Results ?? [];
            return results.Count > 0 ? results[0] : null;
        }

        var task = await Poll(TaskEntry, t => t?.Status is { } s && Terminal.Contains(s), timeoutSeconds: 60, intervalSeconds: 2);
        Ensure(task?.Status == "COMPLETED", $"move task did not complete: {Describe(task)}");
        Ensure(task.DestinationArn == thirdArn,
            $"task recorded DestinationArn '{task.DestinationArn}', expected '{thirdArn}'");

        var srcCount = await Poll(
            async () => (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = srcUrl, AttributeNames = ["ApproximateNumberOfMessages"] })).Attributes!["ApproximateNumberOfMessages"],
            v => v == "0",
            timeoutSeconds: 15,
            intervalSeconds: 2);
        Ensure(srcCount == "0",
            "original source must never receive the move despite being the DLQ's configured " +
            $"source, got ApproximateNumberOfMessages='{srcCount}'");

        var landed = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = thirdUrl, MaxNumberOfMessages = 1, WaitTimeSeconds = 5 })).Messages ?? [];
        Ensure(landed.Count == 1 && landed[0].Body == body,
            $"expected the moved message '{body}' at the explicit third-queue destination, " +
            $"got [{string.Join(", ", landed.Select(m => m.Body))}]");
        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = thirdUrl, ReceiptHandle = landed[0].ReceiptHandle });

        var dlqCount = await Poll(
            async () => (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = dlqUrl, AttributeNames = ["ApproximateNumberOfMessages"] })).Attributes!["ApproximateNumberOfMessages"],
            v => v == "0",
            timeoutSeconds: 30,
            intervalSeconds: 2);
        Ensure(dlqCount == "0", $"DLQ must be drained by the completed move, got '{dlqCount}'");
    }

    private static async Task<string> QueueArn(AmazonSQSClient sqs, string url)
        => (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];

    private static string Describe(ListMessageMoveTasksResultEntry? t)
        => t is null
            ? "{}"
            : $"{{'TaskHandle': '{t.TaskHandle}', 'Status': '{t.Status}', 'SourceArn': '{t.SourceArn}', 'DestinationArn': '{t.DestinationArn}', " +
              $"'ApproximateNumberOfMessagesMoved': {t.ApproximateNumberOfMessagesMoved}, 'FailureReason': '{t.FailureReason}'}}";

    private static async Task<T> Poll<T>(Func<Task<T>> fn, Func<T, bool> pred, double timeoutSeconds, double intervalSeconds = 2.0)
    {
        var deadline = FidelityTime.StartNew();
        var val = await fn();
        while (!pred(val))
        {
            if (deadline.Elapsed >= TimeSpan.FromSeconds(timeoutSeconds))
            {
                return val;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
            val = await fn();
        }

        return val;
    }
}

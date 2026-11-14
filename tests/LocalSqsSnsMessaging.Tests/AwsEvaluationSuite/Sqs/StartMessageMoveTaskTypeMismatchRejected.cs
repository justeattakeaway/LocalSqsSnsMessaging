// Translated from behavioral/sqs/check_start_message_move_task_type_mismatch_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using System.Text.Json.Nodes;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.start-message-move-task-type-mismatch-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class StartMessageMoveTaskTypeMismatchRejected
{
    private static Dictionary<string, string> Tags() => new() { ["sim2real:managed"] = "true", ["sim2real"] = "mmt-type-mismatch-rejected" };

    // A FIFO DLQ redirected at a standard destination is
    // refused with this exact message, before any task is created.
    private const string MismatchMessage = "The source queue and destination queue must be of the same queue type.";

    /// <summary>
    /// StartMessageMoveTask from a FIFO DLQ to a standard queue is rejected with
    /// InvalidParameterValue and its exact message before any task is created, while the FIFO
    /// DLQ's original FIFO source as destination is accepted.
    /// </summary>
    [Test]
    [DisplayName("sqs.start-message-move-task-type-mismatch-rejected")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var token = Guid.NewGuid().ToString("N")[..8];

        var dlqUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-dlq-") + ".fifo",
            Attributes = new() { ["FifoQueue"] = "true" },
            Tags = Tags(),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(dlqUrl));
        var dlqArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = dlqUrl, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];

        var srcUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-src-") + ".fifo",
            Attributes = new()
            {
                ["FifoQueue"] = "true",
                ["RedrivePolicy"] = new JsonObject { ["deadLetterTargetArn"] = dlqArn, ["maxReceiveCount"] = 1 }.ToJsonString(),
            },
            Tags = Tags(),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(srcUrl));
        var srcArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = srcUrl, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];

        var mismatchedUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-std-"), Tags = Tags() })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(mismatchedUrl));
        var mismatchedArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = mismatchedUrl, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];

        var body = $"{token}-mismatch";
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = srcUrl, MessageBody = body, MessageGroupId = "g1", MessageDeduplicationId = token });

        async Task<string> LandedInDlq()
        {
            for (var i = 0; i < 8; i++)
            {
                var r = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = srcUrl, MaxNumberOfMessages = 1, WaitTimeSeconds = 1, VisibilityTimeout = 1 });
                if (r.Messages is null or { Count: 0 })
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromSeconds(2));
            }

            return (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = dlqUrl,
                AttributeNames = ["ApproximateNumberOfMessages"],
            })).Attributes!["ApproximateNumberOfMessages"];
        }

        var landed = await Poll(LandedInDlq, v => v == "1", timeoutSeconds: 30, intervalSeconds: 3);
        Ensure(landed == "1",
            $"precondition: '{body}' must have redriven into the DLQ before the move task is " +
            $"started; the DLQ's ApproximateNumberOfMessages read '{landed}'");

        var (code, message) = await Error(() => sqs.StartMessageMoveTaskAsync(new StartMessageMoveTaskRequest { SourceArn = dlqArn, DestinationArn = mismatchedArn }));
        Ensure(code == "InvalidParameterValue", $"expected InvalidParameterValue, got '{code}'");
        Ensure(message == MismatchMessage, $"unexpected refusal message '{message}'");

        var tasksAfterReject = (await sqs.ListMessageMoveTasksAsync(new ListMessageMoveTasksRequest { SourceArn = dlqArn })).Results ?? [];
        Ensure(tasksAfterReject.Count == 0,
            $"the rejected call left move task(s) behind: [{string.Join(", ", tasksAfterReject.Select(t => $"{t.TaskHandle}:{t.Status}"))}]");

        // Positive control: a same-type destination (the original FIFO source) is accepted.
        var handle = (await sqs.StartMessageMoveTaskAsync(new StartMessageMoveTaskRequest { SourceArn = dlqArn, DestinationArn = srcArn })).TaskHandle;
        Ensure(!string.IsNullOrEmpty(handle), "same-type destination unexpectedly produced no TaskHandle");
    }

    /// <summary>
    /// Call fn(); return its botocore (Error.Code, Error.Message). An unexpected SUCCESS raises,
    /// because an emulator accepting input real AWS refuses is the strongest signal available.
    /// </summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task<StartMessageMoveTaskResponse>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected a ClientError, but the call SUCCEEDED and returned {{'TaskHandle': '{result!.TaskHandle}'}}");
    }

    private static async Task<T> Poll<T>(Func<Task<T>> fn, Func<T, bool> pred, double timeoutSeconds, double intervalSeconds = 2.0)
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
}

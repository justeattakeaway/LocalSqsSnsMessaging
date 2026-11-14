// Translated from behavioral/sqs/check_start_message_move_task_invalid_rate_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using System.Text.Json.Nodes;
using Amazon.Runtime;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.start-message-move-task-invalid-rate-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class StartMessageMoveTaskInvalidRateRejected
{
    /// <summary>
    /// StartMessageMoveTask with MaxNumberOfMessagesPerSecond above the documented 500
    /// cap is rejected before any move task is created, while a valid rate does create one.
    /// </summary>
    [Test]
    [DisplayName("sqs.start-message-move-task-invalid-rate-rejected")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.MoveTasks, "ListDeadLetterSourceQueues never lists the source queue, so the check runs out of its 60s budget waiting for the DLQ association")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;

        var dlqUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-mmtdlq-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(dlqUrl));
        var dlqArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = dlqUrl, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];

        var srcUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-mmtsrc-"),
            Attributes = new() { ["RedrivePolicy"] = new JsonObject { ["deadLetterTargetArn"] = dlqArn, ["maxReceiveCount"] = 1 }.ToJsonString() },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(srcUrl));

        // wait for the DLQ association to register (SQS attribute propagation, up to ~60s:
        // https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/sqs-dead-letter-queues.html)
        await PollUntil(
            async () => ((await sqs.ListDeadLetterSourceQueuesAsync(new ListDeadLetterSourceQueuesRequest { QueueUrl = dlqUrl })).QueueUrls ?? []).Contains(srcUrl),
            timeoutSeconds: 90,
            intervalSeconds: 3);

        var (code, message) = await Error(() => sqs.StartMessageMoveTaskAsync(new StartMessageMoveTaskRequest { SourceArn = dlqArn, MaxNumberOfMessagesPerSecond = 501 }));
        Ensure(code == "InvalidParameterValue",
            $"expected InvalidParameterValue for MaxNumberOfMessagesPerSecond=501, got '{code}'");
        Ensure(message ==
            "Value for parameter MaxNumberOfMessagesPerSecond is invalid. " +
            "Reason: You must enter a number that's between 1 and 500.",
            $"unexpected refusal message '{message}'");

        // effect absent: no move task was created by the rejected call
        var tasksAfterReject = (await sqs.ListMessageMoveTasksAsync(new ListMessageMoveTasksRequest { SourceArn = dlqArn })).Results ?? [];
        Ensure(tasksAfterReject.Count == 0,
            $"the rejected call left move task(s) behind: [{string.Join(", ", tasksAfterReject.Select(t => $"{t.TaskHandle}:{t.Status}"))}]");

        // positive control: an in-range rate is accepted and a task is created
        var handle = (await sqs.StartMessageMoveTaskAsync(new StartMessageMoveTaskRequest { SourceArn = dlqArn, MaxNumberOfMessagesPerSecond = 10 })).TaskHandle;

        async Task Cancel()
        {
            try
            {
                await sqs.CancelMessageMoveTaskAsync(new CancelMessageMoveTaskRequest { TaskHandle = handle });
            }
            catch (AmazonServiceException exc) when (exc.ErrorCode == "ResourceNotFoundException")
            {
            }
        }

        ctx.OnCleanup(Cancel);
        var tasksAfterControl = (await sqs.ListMessageMoveTasksAsync(new ListMessageMoveTasksRequest { SourceArn = dlqArn })).Results ?? [];
        // dlq_arn is a freshly created queue, so the control task is the only move task that
        // can ever be associated with it.
        Ensure(tasksAfterControl.Count == 1,
            "expected exactly 1 move task on a freshly created DLQ after the positive control, " +
            $"got [{string.Join(", ", tasksAfterControl.Select(t => $"{t.TaskHandle}:{t.Status}"))}]");
    }

    /// <summary>
    /// Call fn(); return its botocore (Error.Code, Error.Message). An unexpected SUCCESS raises, because
    /// an emulator ACCEPTING input real AWS refuses is the strongest signal available.
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

    private static async Task PollUntil(Func<Task<bool>> predicate, double timeoutSeconds = 60.0, double intervalSeconds = 2.0)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            if (await predicate())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"condition not met within {timeoutSeconds}s");
    }
}

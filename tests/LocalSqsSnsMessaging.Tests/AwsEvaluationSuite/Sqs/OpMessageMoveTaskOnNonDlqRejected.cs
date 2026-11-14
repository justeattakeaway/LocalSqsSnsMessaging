// Translated from behavioral/sqs/check_op_message_move_task_on_non_dlq_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.op-message-move-task-on-non-dlq-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class OpMessageMoveTaskOnNonDlqRejected
{
    // Exact messages real SQS returns for these refusals.
    private const string MsgNotDlq = "Source queue must be configured as a Dead Letter Queue.";

    /// <summary>Call fn(); return its botocore Error dict. An unexpected SUCCESS raises.</summary>
    private static async Task<AwsError> ClientError<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error ?? throw Fail($"expected rejection but call succeeded and returned {result}");
    }

    private static string RedrivePolicy(string dlqArn, int maxReceiveCount = 2)
        => $"{{\"deadLetterTargetArn\": \"{dlqArn}\", \"maxReceiveCount\": {maxReceiveCount}}}";

    /// <summary>
    /// RedrivePolicy wiring propagates asynchronously, so a freshly-configured DLQ can
    /// still answer InvalidParameterValue for a few seconds. Retry only on that code with the
    /// not-a-DLQ message; any other rejection is a real failure and raises immediately.
    /// </summary>
    private static async Task<StartMessageMoveTaskResponse> StartMoveTaskEventually(AmazonSQSClient sqs, string arn, double timeoutSeconds = 20.0, double intervalSeconds = 2.0)
    {
        var deadline = Stopwatch.StartNew();
        AwsError? lastError = null;
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            var (resp, error) = await FidelityContext.Capture(() => sqs.StartMessageMoveTaskAsync(new StartMessageMoveTaskRequest { SourceArn = arn }));
            if (error is null)
            {
                return resp!;
            }

            lastError = error;
            if (lastError.Code != "InvalidParameterValue" || lastError.Message != MsgNotDlq)
            {
                throw new FidelityAssertionException($"rejected unexpectedly: {lastError}", lastError.Exception);
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"still rejected after {timeoutSeconds}s: {lastError}");
    }

    /// <summary>
    /// StartMessageMoveTask rejects a source queue that is no queue's dead-letter queue, and accepts
    /// the same call once that queue is wired as a DLQ, so the refusal tracks DLQ status.
    /// </summary>
    [Test]
    [DisplayName("sqs.op-message-move-task-on-non-dlq-rejected")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;

        var candidateUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(candidateUrl));
        var candidateArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = candidateUrl, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];

        // Positive control: an independently-wired, already-valid DLQ accepts the call, so a
        // target that blanket-denies StartMessageMoveTask cannot pass this check.
        var controlDlqUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(controlDlqUrl));
        var controlDlqArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = controlDlqUrl, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];
        var controlSrcUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-"),
            Attributes = new() { ["RedrivePolicy"] = RedrivePolicy(controlDlqArn) },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(controlSrcUrl));
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = controlDlqUrl, MessageBody = "positive-control" });

        var resp = await StartMoveTaskEventually(sqs, controlDlqArn);
        Ensure(!string.IsNullOrEmpty(resp.TaskHandle), "positive control returned no TaskHandle");

        // Break: candidate_arn is not referenced by any queue's RedrivePolicy, so it is not
        // a dead-letter queue.
        var error = await ClientError(() => sqs.StartMessageMoveTaskAsync(new StartMessageMoveTaskRequest { SourceArn = candidateArn }));
        Ensure(error.Code == "InvalidParameterValue", $"got {error}");
        Ensure(error.Message == MsgNotDlq, $"got {error}");

        // Effect absent: no task was started for the rejected candidate.
        var tasks = (await sqs.ListMessageMoveTasksAsync(new ListMessageMoveTasksRequest { SourceArn = candidateArn })).Results ?? [];
        Ensure(tasks.Count == 0, $"expected no move tasks for non-DLQ candidate, got [{string.Join(", ", tasks.Select(t => t.Status))}]");

        // Restore: wire another queue's RedrivePolicy at the same candidate ARN, making it a
        // real DLQ, then re-issue the identical call and demand success.
        var restoringSrcUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-"),
            Attributes = new() { ["RedrivePolicy"] = RedrivePolicy(candidateArn) },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(restoringSrcUrl));
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = candidateUrl, MessageBody = "restored" });

        resp = await StartMoveTaskEventually(sqs, candidateArn, timeoutSeconds: 90.0, intervalSeconds: 3.0);
        Ensure(!string.IsNullOrEmpty(resp.TaskHandle), "restored DLQ returned no TaskHandle");
    }
}

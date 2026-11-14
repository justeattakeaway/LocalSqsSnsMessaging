// Translated from behavioral/sqs/check_op_cancel_unknown_move_task_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using System.Text;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.op-cancel-unknown-move-task-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class OpCancelUnknownMoveTaskRejected
{
    private const string UnknownTaskMessage = "Task does not exist.";

    // MaxNumberOfMessagesPerSecond=1 over _N=40 messages gives a ~40s RUNNING window, so the
    // positive-control cancel below lands on a task that is still moving messages.
    private const int N = 40;
    private const int Rate = 1;
    private static readonly string[] TerminalStatuses = ["CANCELLED", "COMPLETED", "FAILED"];

    private static Dictionary<string, string> Tags => new()
    {
        ["sim2real:managed"] = "true",
        ["sim2real"] = "op-cancel-unknown-move-task-rejected",
    };

    private static async Task<AwsError> ClientError<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error ?? throw Fail($"expected CancelMessageMoveTask to be rejected, got a response: {result}");
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

    private static async Task<ListMessageMoveTasksResultEntry?> TaskEntry(AmazonSQSClient sqs, string sourceArn)
    {
        var results = (await sqs.ListMessageMoveTasksAsync(new ListMessageMoveTasksRequest { SourceArn = sourceArn, MaxResults = 10 })).Results ?? [];
        return results.Count == 1 ? results[0] : null;
    }

    private static string Show(ListMessageMoveTasksResultEntry? t)
        => t is null ? "{}" : $"{{Status: {t.Status}, SourceArn: {t.SourceArn}, DestinationArn: {t.DestinationArn}, FailureReason: {t.FailureReason}}}";

    /// <summary>
    /// CancelMessageMoveTask rejects a well-formed handle naming an unknown task id with
    /// ResourceNotFoundException, after cancelling a real RUNNING task succeeds (so it does not refuse
    /// every cancel); the real task's CANCELLED record is left unchanged.
    /// </summary>
    [Test]
    [DisplayName("sqs.op-cancel-unknown-move-task-rejected")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;

        var dlqUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-") + "-dlq", Tags = Tags })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(dlqUrl));
        var dlqArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = dlqUrl, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];

        var destUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-") + "-dest", Tags = Tags })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(destUrl));
        var destArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = destUrl, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];

        // StartMessageMoveTask refuses a source that is not a dead-letter queue for any queue, so this
        // RedrivePolicy is what makes dlq_url eligible as a move-task source.
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = destUrl,
            Attributes = new() { ["RedrivePolicy"] = $"{{\"deadLetterTargetArn\": \"{dlqArn}\", \"maxReceiveCount\": 1}}" },
        });

        for (var i = 0; i < N; i++)
        {
            await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = dlqUrl, MessageBody = $"cancel-unknown-positive-control-{i}" });
        }

        var realHandle = (await sqs.StartMessageMoveTaskAsync(new StartMessageMoveTaskRequest
        {
            SourceArn = dlqArn,
            DestinationArn = destArn,
            MaxNumberOfMessagesPerSecond = Rate,
        })).TaskHandle ?? throw new KeyNotFoundException("TaskHandle");

        var running = await Poll(() => TaskEntry(sqs, dlqArn), r => r?.Status == "RUNNING", timeoutSeconds: 60, intervalSeconds: 1.0);
        Ensure(running?.Status == "RUNNING",
            $"positive control: the move task was never observed RUNNING, so cancelling it below " +
            $"would prove nothing: ListMessageMoveTasks reported {Show(running)}");

        // Positive control: CancelMessageMoveTask against THIS real, running task must succeed.
        await sqs.CancelMessageMoveTaskAsync(new CancelMessageMoveTaskRequest { TaskHandle = realHandle });

        var settled = await Poll(() => TaskEntry(sqs, dlqArn), r => TerminalStatuses.Contains(r?.Status), timeoutSeconds: 120, intervalSeconds: 2.0);
        Ensure(settled?.Status == "CANCELLED",
            $"positive control: cancelling a real running task must settle on CANCELLED, got " +
            $"'{settled?.Status}' (ListMessageMoveTasks reported {Show(settled)})");

        // Now break exactly one thing: a task handle shaped like a real one (same encoding, a real
        // queue ARN) but naming a task id that was never issued.
        var bogus = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"{{\"taskId\": \"00000000-0000-0000-0000-000000000000\", \"sourceArn\": \"{dlqArn}\"}}"));

        var error = await ClientError(() => sqs.CancelMessageMoveTaskAsync(new CancelMessageMoveTaskRequest { TaskHandle = bogus }));
        Ensure(error.Code == "ResourceNotFoundException",
            $"CancelMessageMoveTask on a handle naming an unknown task id must fail with " +
            $"ResourceNotFoundException, got '{error.Code}'");
        Ensure(error.Message == UnknownTaskMessage, $"expected message '{UnknownTaskMessage}', got '{error.Message}'");

        // Effect-absent, via an independent read: the rejected cancel on a bogus handle must not
        // have disturbed the real task's own terminal record.
        var after = await TaskEntry(sqs, dlqArn);
        Ensure(after?.Status == "CANCELLED",
            $"the rejected cancel on a bogus handle changed the real task's recorded status: " +
            $"before=CANCELLED after={Show(after)}");
    }
}

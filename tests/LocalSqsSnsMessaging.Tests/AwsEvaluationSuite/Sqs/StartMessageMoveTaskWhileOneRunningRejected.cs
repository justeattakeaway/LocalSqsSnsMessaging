// Translated from behavioral/sqs/check_start_message_move_task_while_one_running_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.start-message-move-task-while-one-running-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class StartMessageMoveTaskWhileOneRunningRejected
{
    private const int N = 30;
    private const string MsgBusy = "There is already a task running. Only one active task is allowed for a source queue " +
                                   "arn at a given time.";
    private static readonly HashSet<string> Terminal = new(StringComparer.Ordinal) { "COMPLETED", "CANCELLED", "FAILED" };

    private static Dictionary<string, string> Tags() => new() { ["sim2real"] = "start-message-move-task-while-one-running-rejected" };

    /// <summary>
    /// A second StartMessageMoveTask on a DLQ with a task RUNNING is refused and adds no task; once
    /// the first is cancelled and settles, a new task starts, so one DLQ never has two movers.
    /// </summary>
    [Test]
    [DisplayName("sqs.start-message-move-task-while-one-running-rejected")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var @base = FidelityContext.UniqueName("sim2real-behav-mmx-");
        var srcUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = @base + "-src", Tags = Tags() })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(srcUrl));
        var srcArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = srcUrl, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];
        var dlqUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = @base + "-dlq", Tags = Tags() })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(dlqUrl));
        var dlqArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = dlqUrl, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = srcUrl,
            Attributes = new() { ["RedrivePolicy"] = new JsonObject { ["deadLetterTargetArn"] = dlqArn, ["maxReceiveCount"] = 1 }.ToJsonString() },
        });
        for (var i = 0; i < N; i += 10)
        {
            var offset = i;
            await sqs.SendMessageBatchAsync(new SendMessageBatchRequest
            {
                QueueUrl = dlqUrl,
                Entries = [.. Enumerable.Range(0, 10).Select(j => new SendMessageBatchRequestEntry { Id = j.ToString(CultureInfo.InvariantCulture), MessageBody = $"mmx-{offset + j}" })],
            });
        }

        async Task<int> Depth()
            => int.Parse((await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = dlqUrl,
                AttributeNames = ["ApproximateNumberOfMessages"],
            })).Attributes!["ApproximateNumberOfMessages"], CultureInfo.InvariantCulture);

        Ensure(await Poll(Depth, d => d == N, 60, 2.0) == N, "the DLQ never showed all messages");

        // MaxNumberOfMessagesPerSecond=1 keeps the first task RUNNING for ~_N seconds.
        var first = await sqs.StartMessageMoveTaskAsync(new StartMessageMoveTaskRequest { SourceArn = dlqArn, DestinationArn = srcArn, MaxNumberOfMessagesPerSecond = 1 });
        var handle = first.TaskHandle ?? throw new KeyNotFoundException("'TaskHandle'");
        var cancelled = new List<bool>();

        async Task Cancel()
        {
            if (cancelled.Count == 0)
            {
                try
                {
                    await sqs.CancelMessageMoveTaskAsync(new CancelMessageMoveTaskRequest { TaskHandle = handle });
                }
                catch (AmazonServiceException exc) when (exc.ErrorCode == "ResourceNotFoundException")
                {
                }
            }
        }

        ctx.OnCleanup(Cancel);
        var running = await Poll(() => Tasks(sqs, dlqArn), r => r.Count == 1 && r[0].Status == "RUNNING", 30);
        Ensure(running.Count == 1 && running[0].Status == "RUNNING",
            $"the first task was never observed RUNNING: {Statuses(running)}");

        var (code, msg) = await Err(() => sqs.StartMessageMoveTaskAsync(new StartMessageMoveTaskRequest { SourceArn = dlqArn, DestinationArn = srcArn }));
        Ensure(code == "InvalidParameterValue", $"a second start on a busy source gave code '{code}'");
        Ensure(msg == MsgBusy, $"a second start on a busy source gave message '{msg}'");

        var listed = await Tasks(sqs, dlqArn);
        Ensure(listed.Count == 1,
            $"the refused second start added a task: ListMessageMoveTasks shows {listed.Count} " +
            $"({Statuses(listed)})");

        await sqs.CancelMessageMoveTaskAsync(new CancelMessageMoveTaskRequest { TaskHandle = handle });
        cancelled.Add(true);
        var settled = await Poll(() => Tasks(sqs, dlqArn), r => r.Count > 0 && r[0].Status is { } s && Terminal.Contains(s), 120, 2.0);
        Ensure(settled.Count > 0 && settled[0].Status == "CANCELLED",
            "the cancelled task did not settle on CANCELLED: " +
            $"[{string.Join(", ", settled.Select(t => $"('{t.Status}', '{t.FailureReason}')"))}]");

        var second = await sqs.StartMessageMoveTaskAsync(new StartMessageMoveTaskRequest { SourceArn = dlqArn, DestinationArn = srcArn });
        var secondHandle = second.TaskHandle ?? throw new KeyNotFoundException("'TaskHandle'");
        Ensure(secondHandle != handle, "the new task reused the cancelled task's handle");
        var done = await Poll(() => Tasks(sqs, dlqArn), r => r.Count > 0 && r[0].Status is { } s && Terminal.Contains(s), 120, 2.0);
        Ensure(done.Count > 0 && done[0].Status == "COMPLETED",
            "positive control: the task started after the cancel did not complete: " +
            $"[{string.Join(", ", done.Select(t => $"('{t.Status}', '{t.FailureReason}')"))}]");
        Ensure(done.Count == 2,
            $"after one cancelled and one completed task the source lists {done.Count} tasks, expected 2");
    }

    private static string Statuses(List<ListMessageMoveTasksResultEntry> tasks)
        => "[" + string.Join(", ", tasks.Select(t => $"'{t.Status}'")) + "]";

    /// <summary>(Code, Message) of the ClientError <paramref name="fn"/> raises; an unexpected success raises.</summary>
    private static async Task<(string? Code, string? Message)> Err(Func<Task<StartMessageMoveTaskResponse>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be refused, but it succeeded: {{'TaskHandle': '{result!.TaskHandle}'}}");
    }

    private static async Task<T> Poll<T>(Func<Task<T>> fn, Func<T, bool> pred, double timeoutSeconds, double intervalSeconds = 1.0)
    {
        var deadline = Stopwatch.StartNew();
        var val = await fn();
        while (!pred(val) && deadline.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds));
            val = await fn();
        }

        return val;
    }

    private static async Task<List<ListMessageMoveTasksResultEntry>> Tasks(AmazonSQSClient sqs, string arn)
        => (await sqs.ListMessageMoveTasksAsync(new ListMessageMoveTasksRequest { SourceArn = arn, MaxResults = 10 })).Results ?? [];
}

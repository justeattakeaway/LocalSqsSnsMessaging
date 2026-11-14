// Translated from behavioral/sqs/check_cancel_message_move_task_conserves_messages.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.cancel-message-move-task-conserves-messages</summary>
[Category("AwsEvaluationSuite")]
public sealed class CancelMessageMoveTaskConservesMessages
{
    private static readonly Dictionary<string, string> Tags = new() { ["sim2real:managed"] = "true", ["sim2real"] = "cancel-message-move-task" };

    private const int N = 40;
    // Throttling to 1/s keeps the task RUNNING long enough to cancel; a cancel a few seconds in still
    // lets ~15-25 messages through, so 40 leaves headroom for `moved < _N`.
    private const int Rate = 1;
    // A cancel passes through CANCELLING for ~10-20s while the moved counter keeps rising, so
    // CANCELLING is not terminal.
    private static readonly HashSet<string> Terminal = ["COMPLETED", "CANCELLED", "FAILED"];

    /// <summary>
    /// Cancelling a RUNNING message-move task (ApproximateNumberOfMessagesToMove in 1..N) settles on
    /// CANCELLED, moves fewer than all messages, and the DLQ + destination depths poll to N.
    ///
    /// Reference: https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_CancelMessageMoveTask.html
    /// </summary>
    [Test]
    [DisplayName("sqs.cancel-message-move-task-conserves-messages")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.MoveTasks, "a cancelled move task never settles on a terminal status; the check times out after 60s")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var token = Guid.NewGuid().ToString("N")[..8];

        // StartMessageMoveTask only accepts a source that is some queue's DLQ, hence the RedrivePolicy.
        var urlDest = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-") + "-dest", Tags = new(Tags),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(urlDest));
        var destArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = urlDest, AttributeNames = ["QueueArn"],
        })).Attributes!["QueueArn"];

        var urlDlq = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-") + "-dlq", Tags = new(Tags),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(urlDlq));
        var dlqArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = urlDlq, AttributeNames = ["QueueArn"],
        })).Attributes!["QueueArn"];

        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = urlDest,
            Attributes = new()
            {
                ["RedrivePolicy"] = JsonSerializer.Serialize(new Dictionary<string, object> { ["deadLetterTargetArn"] = dlqArn, ["maxReceiveCount"] = 1 }),
            },
        });

        for (var i = 0; i < N; i++)
        {
            await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = urlDlq, MessageBody = $"move-{token}-{i}" });
        }

        var dlqDepth = await Poll(() => Depth(sqs, urlDlq), v => v == N, timeoutS: 90);
        Ensure(dlqDepth == N,
            $"only {dlqDepth} of {N} messages landed on the DLQ, so the conservation assertion " +
            "below would be measured against the wrong total");

        var handle = (await sqs.StartMessageMoveTaskAsync(new StartMessageMoveTaskRequest
        {
            SourceArn = dlqArn, DestinationArn = destArn, MaxNumberOfMessagesPerSecond = Rate,
        })).TaskHandle;

        // ApproximateNumberOfMessagesToMove is absent from the first RUNNING records, so wait for it.
        var running = await Poll(() => TaskFor(sqs, dlqArn),
            r => r?.Status == "RUNNING" && r.ApproximateNumberOfMessagesToMove is not null,
            timeoutS: 60, intervalS: 1.0);
        Ensure(running?.Status == "RUNNING",
            "the move task was never observed RUNNING, so the cancel below cancelled nothing: " +
            $"ListMessageMoveTasks reported {Describe(running)}");
        // ApproximateNumberOfMessagesToMove is a snapshot of the source taken when the task starts
        // (ListMessageMoveTasksResultEntry docs) and is approximate, so it is bounded by the _N
        // messages the source holds rather than pinned to _N.
        var toMove = running.ApproximateNumberOfMessagesToMove;
        Ensure(toMove is > 0 and <= N,
            $"the running task does not claim to be moving between 1 and the {N} messages that were " +
            $"enqueued: ApproximateNumberOfMessagesToMove={toMove?.ToString(CultureInfo.InvariantCulture) ?? "None"}");
        Ensure(running.TaskHandle == handle,
            "the RUNNING task's TaskHandle is not the one StartMessageMoveTask returned: " +
            $"'{running.TaskHandle}' != '{handle}'");

        await sqs.CancelMessageMoveTaskAsync(new CancelMessageMoveTaskRequest { TaskHandle = handle });

        var final = await Poll(() => TaskFor(sqs, dlqArn),
            r => r?.Status is { } s && Terminal.Contains(s),
            timeoutS: 120, intervalS: 2.0);
        var finalStatus = final?.Status;
        Ensure(finalStatus == "CANCELLED",
            "a cancelled move task must settle on the terminal status CANCELLED, got " +
            $"'{finalStatus}' (FailureReason='{final?.FailureReason}'). CANCELLING here means " +
            "the task never settled; COMPLETED means the cancel did not stop it");

        var moved = final!.ApproximateNumberOfMessagesMoved;
        Ensure(moved is not null,
            $"ApproximateNumberOfMessagesMoved must be an integer on a terminal task, got {moved}");
        // A task that reports moving all _N messages was not cancelled, whatever status it prints.
        Ensure(moved is >= 0 and < N,
            $"the cancelled task reports moving {moved} of {N} messages -- a cancel that moved " +
            "everything cancelled nothing");

        // Both depths are eventually consistent, so poll for the conserved total.
        async Task<(int Dlq, int Dest)> Both() => (await Depth(sqs, urlDlq), await Depth(sqs, urlDest));

        var (dlqAfter, destAfter) = await Poll(Both, t => t.Dlq + t.Dest == N,
            timeoutS: 120.0, budgetS: BudgetS(ctx, 120.0));
        Ensure(dlqAfter + destAfter == N,
            $"messages were lost or duplicated by the cancelled move: dlq={dlqAfter} " +
            $"dest={destAfter} sum={dlqAfter + destAfter}, started with {N}");
        // Whether any message moved before the cancel landed is timing, so 0 passes.
        Ensure((0 < destAfter && destAfter < N) || (0 <= destAfter && destAfter < N),
            $"a cancelled {N}-message move must leave the destination holding fewer than all of " +
            $"them; it holds {destAfter}");
        // ApproximateNumberOfMessagesMoved is not compared with the destination depth: it lags the
        // queue by several messages.
    }

    private static async Task<T> Poll<T>(Func<Task<T>> fn, Func<T, bool> pred, double timeoutS, double intervalS = 2.0, double? budgetS = null)
    {
        var start = Stopwatch.StartNew();
        var stop = TimeSpan.FromSeconds(budgetS is null ? timeoutS : Math.Min(timeoutS, budgetS.Value));
        var val = await fn();
        while (!pred(val))
        {
            if (start.Elapsed >= stop)
            {
                if (budgetS is not null && budgetS < timeoutS)
                {
                    throw new TimeoutException(
                        $"the check's time budget ran out after {start.Elapsed.TotalSeconds:F1}s, before the " +
                        $"{timeoutS}s wait ended (last={val}); not graded");
                }

                return val;
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalS));
            val = await fn();
        }

        return val;
    }

    /// <summary>A poll's timeout: cap_s, bounded by the runner's remaining time less 5 s.</summary>
    private static double BudgetS(FidelityContext ctx, double capS)
    {
        var left = ctx.TimeLeftSeconds;
        return Math.Max(1.0, Math.Min(capS, left - 5.0));
    }

    /// <summary>
    /// The one move-task result for `source_arn`, or {}.
    ///
    /// Matched on SourceArn because ListMessageMoveTasks omits `TaskHandle` once the task leaves RUNNING.
    /// </summary>
    private static async Task<ListMessageMoveTasksResultEntry?> TaskFor(AmazonSQSClient sqs, string sourceArn)
    {
        var results = (await sqs.ListMessageMoveTasksAsync(new ListMessageMoveTasksRequest { SourceArn = sourceArn, MaxResults = 10 })).Results ?? [];
        return results.Count == 1 ? results[0] : null;
    }

    private static async Task<int> Depth(AmazonSQSClient sqs, string url)
        => int.Parse((await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = url, AttributeNames = ["ApproximateNumberOfMessages"],
        })).Attributes!.GetValueOrDefault("ApproximateNumberOfMessages", "-1"), CultureInfo.InvariantCulture);

    private static string Describe(ListMessageMoveTasksResultEntry? r)
        => r is null ? "{}" : $"{{Status={r.Status}, TaskHandle={r.TaskHandle}, ToMove={r.ApproximateNumberOfMessagesToMove}, Moved={r.ApproximateNumberOfMessagesMoved}}}";
}

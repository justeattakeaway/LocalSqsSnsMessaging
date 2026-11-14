// Translated from behavioral/sqs/check_list_message_move_tasks_max_results_out_of_range_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.list-message-move-tasks-max-results-out-of-range-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListMessageMoveTasksMaxResultsOutOfRangeRejected
{
    private static readonly Dictionary<string, string> Tags = new()
    {
        ["sim2real"] = "list-message-move-tasks-max-results-out-of-range-rejected",
    };

    private const string MsgRange = "Value for parameter MaxResults is invalid. Reason: You must enter a number that's " +
                                    "between 1 and 10.";

    private static readonly HashSet<string> Terminal = ["COMPLETED", "CANCELLED", "FAILED"];

    /// <summary>
    /// ListMessageMoveTasks refuses MaxResults 0 and 11 with InvalidParameterValue and the 1..10
    /// range message on a DLQ with a completed task; MaxResults=10 lists that task as COMPLETED and
    /// both messages end up on the destination.
    /// </summary>
    [Test]
    [DisplayName("sqs.list-message-move-tasks-max-results-out-of-range-rejected")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.MoveTasks, "Check timed out after 60.0s (the move task never reports a terminal status)")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var baseName = FidelityContext.UniqueName("sim2real-behav-lmr-");
        var srcUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = baseName + "-src", Tags = new(Tags) })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(srcUrl));
        var srcArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = srcUrl, AttributeNames = ["QueueArn"],
        })).Attributes!["QueueArn"];
        var dlqUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = baseName + "-dlq", Tags = new(Tags) })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(dlqUrl));
        var dlqArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = dlqUrl, AttributeNames = ["QueueArn"],
        })).Attributes!["QueueArn"];
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = srcUrl,
            Attributes = new() { ["RedrivePolicy"] = RedrivePolicy(dlqArn, 1) },
        });
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = dlqUrl, MessageBody = "lmr-0" });
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = dlqUrl, MessageBody = "lmr-1" });

        var deadline = Stopwatch.StartNew();
        string depth;
        while (true)
        {
            depth = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = dlqUrl, AttributeNames = ["ApproximateNumberOfMessages"],
            })).Attributes!["ApproximateNumberOfMessages"];
            if (depth == "2" || deadline.Elapsed > TimeSpan.FromSeconds(60))
            {
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        Ensure(depth == "2", $"the DLQ shows '{depth}' messages, expected '2'");

        await sqs.StartMessageMoveTaskAsync(new StartMessageMoveTaskRequest { SourceArn = dlqArn, DestinationArn = srcArn });
        deadline = Stopwatch.StartNew();
        List<ListMessageMoveTasksResultEntry> res;
        while (true)
        {
            res = (await sqs.ListMessageMoveTasksAsync(new ListMessageMoveTasksRequest { SourceArn = dlqArn })).Results ?? [];
            if ((res.Count > 0 && res[0].Status is { } status && Terminal.Contains(status)) || deadline.Elapsed > TimeSpan.FromSeconds(120))
            {
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        Ensure(res.Count > 0 && res[0].Status == "COMPLETED",
            $"the move task did not complete: [{string.Join(", ", res.Select(t => (t.Status, t.FailureReason)))}]");

        // The upper bound is 10, not the 1000 of ListQueues.
        foreach (var bad in new[] { 0, 11 })
        {
            var (code, msg) = await Err(() => sqs.ListMessageMoveTasksAsync(new ListMessageMoveTasksRequest { SourceArn = dlqArn, MaxResults = bad }));
            Ensure(code == "InvalidParameterValue", $"MaxResults={bad} gave code '{code}'");
            Ensure(msg == MsgRange, $"MaxResults={bad} gave message '{msg}'");
        }

        var listed = (await sqs.ListMessageMoveTasksAsync(new ListMessageMoveTasksRequest { SourceArn = dlqArn, MaxResults = 10 })).Results ?? [];
        Ensure(listed.Count == 1, $"MaxResults=10 listed {listed.Count} tasks, expected the one task");
        var task = listed[0];
        Ensure(task.Status == "COMPLETED",
            $"the listed task reads Status='{task.Status}', expected COMPLETED");
        Ensure((task.SourceArn, task.DestinationArn) == (dlqArn, srcArn),
            "the listed task does not name the DLQ as source and the source queue as destination");
        // ApproximateNumberOfMessagesMoved on a COMPLETED record can still read 0, so the
        // move is graded by where the messages are, not by the approximate counter.

        async Task<(string, string)> Depths()
        {
            var results = new List<string>();
            foreach (var u in new[] { dlqUrl, srcUrl })
            {
                results.Add((await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
                {
                    QueueUrl = u, AttributeNames = ["ApproximateNumberOfMessages"],
                })).Attributes!["ApproximateNumberOfMessages"]);
            }

            return (results[0], results[1]);
        }

        deadline = Stopwatch.StartNew();
        var depths = await Depths();
        while (depths != ("0", "2") && deadline.Elapsed < TimeSpan.FromSeconds(60))
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            depths = await Depths();
        }

        Ensure(depths == ("0", "2"),
            $"after the completed move the DLQ and destination hold {depths}, expected ('0', '2')");
    }

    /// <summary>(Code, Message) of the ClientError ``fn`` raises; an unexpected success raises.</summary>
    private static async Task<(string? Code, string? Message)> Err<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }

    /// <summary><c>json.dumps({"deadLetterTargetArn": arn, "maxReceiveCount": n})</c>, byte for byte.</summary>
    private static string RedrivePolicy(string deadLetterTargetArn, int maxReceiveCount) =>
        $"{{\"deadLetterTargetArn\": \"{deadLetterTargetArn}\", \"maxReceiveCount\": {maxReceiveCount}}}";
}

// Translated from behavioral/sqs/check_redrive_allow_policy_denyall_does_not_block_start_message_move_task.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.redrive-allow-policy-denyall-does-not-block-start-message-move-task</summary>
[Category("AwsEvaluationSuite")]
public sealed class RedriveAllowPolicyDenyallDoesNotBlockStartMessageMoveTask
{
    private static readonly HashSet<string> Terminal = ["COMPLETED", "FAILED", "CANCELLED"];

    private static Dictionary<string, string> Tags() => new() { ["sim2real:managed"] = "true", ["sim2real"] = "rap-denyall-vs-move-task" };

    /// <summary>
    /// Once a DLQ's RedriveAllowPolicy reads back denyAll after its source is wired,
    /// StartMessageMoveTask still completes and returns the DLQ's message to that source, because
    /// RedriveAllowPolicy gates new RedrivePolicy associations, not the move API.
    /// </summary>
    [Test]
    [DisplayName("sqs.redrive-allow-policy-denyall-does-not-block-start-message-move-task")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var token = Guid.NewGuid().ToString("N")[..8];

        var dlqUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-dlq-"), Tags = Tags() })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(dlqUrl));
        var dlqArn = await QueueArn(sqs, dlqUrl);

        var srcUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-src-"), Tags = Tags() })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(srcUrl));
        var srcArn = await QueueArn(sqs, srcUrl);

        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = dlqUrl,
            Attributes = new()
            {
                ["RedriveAllowPolicy"] = new JsonObject { ["redrivePermission"] = "byQueue", ["sourceQueueArns"] = new JsonArray(srcArn) }.ToJsonString(),
            },
        });
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = srcUrl,
            Attributes = new()
            {
                ["RedrivePolicy"] = new JsonObject { ["deadLetterTargetArn"] = dlqArn, ["maxReceiveCount"] = 1 }.ToJsonString(),
            },
        });

        var body = $"{token}-retro";
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = srcUrl, MessageBody = body });

        async Task<string> LandedInDlq()
        {
            for (var i = 0; i < 8; i++)
            {
                var r = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
                {
                    QueueUrl = srcUrl,
                    MaxNumberOfMessages = 1,
                    WaitTimeSeconds = 1,
                    VisibilityTimeout = 1,
                });
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

        var landed = await Poll(LandedInDlq, v => v == "1", timeoutS: 90.0, budgetS: BudgetS(ctx, 90.0), intervalS: 3);
        Ensure(landed == "1",
            $"precondition: '{body}' must have redriven into the DLQ before the move task is "
            + $"started; the DLQ's ApproximateNumberOfMessages read '{landed}'");

        // Revoke the allow policy AFTER the association was already used.
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = dlqUrl,
            Attributes = new() { ["RedriveAllowPolicy"] = new JsonObject { ["redrivePermission"] = "denyAll" }.ToJsonString() },
        });

        // SetQueueAttributes changes can take up to 60 s to propagate (SetQueueAttributes docs), so
        // the move starts only once the DLQ reads back denyAll; otherwise it would test nothing.
        async Task<string?> RedrivePermission()
        {
            var raw = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = dlqUrl,
                AttributeNames = ["RedriveAllowPolicy"],
            })).Attributes!.GetValueOrDefault("RedriveAllowPolicy", "{}");
            return JsonNode.Parse(raw)!["redrivePermission"]?.GetValue<string>();
        }

        var permission = await Poll(RedrivePermission, v => v == "denyAll", timeoutS: 60.0, budgetS: BudgetS(ctx, 60.0), intervalS: 2);
        Ensure(permission == "denyAll",
            $"the DLQ's RedriveAllowPolicy never read back denyAll; redrivePermission='{permission}'");

        var handle = (await sqs.StartMessageMoveTaskAsync(new StartMessageMoveTaskRequest { SourceArn = dlqArn, DestinationArn = srcArn })).TaskHandle;
        Ensure(!string.IsNullOrEmpty(handle), "StartMessageMoveTask returned no TaskHandle despite denyAll");

        async Task<ListMessageMoveTasksResultEntry?> Task_()
        {
            var results = (await sqs.ListMessageMoveTasksAsync(new ListMessageMoveTasksRequest { SourceArn = dlqArn, MaxResults = 1 })).Results ?? [];
            return results.Count > 0 ? results[0] : null;
        }

        var task = await Poll(Task_, t => t?.Status is { } s && Terminal.Contains(s), timeoutS: 60, intervalS: 2);
        Ensure(task?.Status == "COMPLETED",
            $"move task must complete despite RedriveAllowPolicy=denyAll, got {{Status={task?.Status}, FailureReason={task?.FailureReason}}}");

        var dlqCount = await Poll(
            async () => (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = dlqUrl,
                AttributeNames = ["ApproximateNumberOfMessages"],
            })).Attributes!["ApproximateNumberOfMessages"],
            v => v == "0",
            timeoutS: 90.0, budgetS: BudgetS(ctx, 90.0),
            intervalS: 2);
        Ensure(dlqCount == "0", $"DLQ must be drained by the move despite denyAll, got '{dlqCount}'");

        var back = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = srcUrl, MaxNumberOfMessages = 1, WaitTimeSeconds = 5 })).Messages ?? [];
        Ensure(back.Count == 1 && back[0].Body == body,
            $"expected the message '{body}' moved back to the source despite denyAll, got [{string.Join(", ", back.Select(m => m.Body))}]");
        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = srcUrl, ReceiptHandle = back[0].ReceiptHandle });
    }

    private static async Task<string> QueueArn(IAmazonSQS sqs, string url) =>
        (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];

    private static async Task<T> Poll<T>(Func<Task<T>> fn, Func<T, bool> pred, double timeoutS, double intervalS = 2.0, double? budgetS = null)
    {
        var start = FidelityTime.StartNew();
        var stop = budgetS is null ? timeoutS : Math.Min(timeoutS, budgetS.Value);
        var val = await fn();
        while (!pred(val))
        {
            if (start.Elapsed.TotalSeconds >= stop)
            {
                if (budgetS is not null && budgetS < timeoutS)
                {
                    throw new TimeoutException(
                        $"the check's time budget ran out after {start.Elapsed.TotalSeconds:F1}s, before the "
                        + $"{timeoutS}s wait ended (last={val}); not graded");
                }

                return val;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
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
}

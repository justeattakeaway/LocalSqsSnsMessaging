// Translated from behavioral/sqs/check_e2e_dlq_redrive_full_cycle.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using System.Text.Json;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.e2e-dlq-redrive-full-cycle</summary>
[Category("AwsEvaluationSuite")]
public sealed class E2eDlqRedriveFullCycle
{
    /// <summary>
    /// Messages that exhaust maxReceiveCount land in the DLQ, StartMessageMoveTask redrives them back
    /// to the source queue, and they are consumable there again.
    /// </summary>
    [Test]
    [DisplayName("sqs.e2e-dlq-redrive-full-cycle")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.MoveTasks, "the move task never reports COMPLETED in ListMessageMoveTasks, so the check runs out its 60s budget")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;

        var dlqUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-e2edlq-dlq-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(dlqUrl));
        var dlqArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = dlqUrl, AttributeNames = ["QueueArn"] }))
            .Attributes!["QueueArn"];

        var srcUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-e2edlq-src-"),
            Attributes = new()
            {
                ["VisibilityTimeout"] = "1",
                ["RedrivePolicy"] = PyDumps(("deadLetterTargetArn", dlqArn), ("maxReceiveCount", 1)),
            },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(srcUrl));
        var srcArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = srcUrl, AttributeNames = ["QueueArn"] }))
            .Attributes!["QueueArn"];

        var body = $"redrive-cycle-{srcUrl}";
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = srcUrl, MessageBody = body });

        var first = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = srcUrl, MaxNumberOfMessages = 1, WaitTimeSeconds = 5 });
        Ensure(first.Messages is { Count: > 0 }, "expected the first receive to return the message");
        // deliberately do not delete it, so the 1s visibility timeout lapses and the
        // next receive attempt exceeds maxReceiveCount=1, moving it to the DLQ

        Message? dlqMessage = null;
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed.TotalSeconds < 40.0 && dlqMessage is null)
        {
            await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = srcUrl, MaxNumberOfMessages = 1, WaitTimeSeconds = 2 });
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = dlqUrl, MaxNumberOfMessages = 1, WaitTimeSeconds = 2 });
            var msgs = resp.Messages ?? [];
            if (msgs.Count > 0)
            {
                dlqMessage = msgs[0];
            }
        }

        Ensure(dlqMessage is not null, "message was never moved to the DLQ");
        Ensure(dlqMessage.Body == body, "assert dlq_message[\"Body\"] == body");
        // keep the DLQ as StartMessageMoveTask needs to find it: present AND visible. The receive
        // above that confirmed arrival left it in-flight (the default 30s VisibilityTimeout), so
        // hand visibility back immediately rather than starting the move against an invisible
        // message.
        await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest
        {
            QueueUrl = dlqUrl,
            ReceiptHandle = dlqMessage.ReceiptHandle,
            VisibilityTimeout = 0,
        });

        // The source queue must have given the message up before the move starts.
        string? srcDepth = null;
        deadline = Stopwatch.StartNew();
        while (deadline.Elapsed.TotalSeconds < 60.0)
        {
            srcDepth = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = srcUrl, AttributeNames = ["ApproximateNumberOfMessages"] }))
                .Attributes!.GetValueOrDefault("ApproximateNumberOfMessages", "?");
            if (srcDepth == "0")
            {
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(2.0));
        }

        Ensure(srcDepth == "0",
            $"source queue still reports {srcDepth} message(s) before the move task starts");

        var move = await sqs.StartMessageMoveTaskAsync(new StartMessageMoveTaskRequest { SourceArn = dlqArn, DestinationArn = srcArn });
        Ensure(!string.IsNullOrEmpty(move.TaskHandle), "assert move.get(\"TaskHandle\")");

        // A COMPLETED entry drops TaskHandle (present only while RUNNING), so also match on SourceArn;
        // matching on TaskHandle alone never sees the terminal entry.
        string? status = null;
        ListMessageMoveTasksResultEntry? lastResult = null;
        deadline = Stopwatch.StartNew();
        while (deadline.Elapsed.TotalSeconds < 600.0)
        {
            var results = (await sqs.ListMessageMoveTasksAsync(new ListMessageMoveTasksRequest { SourceArn = dlqArn })).Results ?? [];
            var matching = results.Where(r => r.TaskHandle == move.TaskHandle || r.SourceArn == dlqArn).ToList();
            if (matching.Count > 0)
            {
                lastResult = matching[0];
                status = lastResult.Status;
                if (status is "COMPLETED" or "FAILED" or "CANCELLED")
                {
                    break;
                }
            }

            // The runner's budget (the test's [Timeout]) ends this 600s wait, as it did in Python.
            await Task.Delay(TimeSpan.FromSeconds(5.0), ctx.CancellationToken);
        }

        Ensure(status == "COMPLETED",
            $"expected move task to complete, got status={status ?? "None"}; last_result={(lastResult is null ? "None" : JsonSerializer.Serialize(lastResult))}");

        // effect on the DLQ itself: a COMPLETED move must have drained it, not just reported success
        var dlqDepth = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = dlqUrl, AttributeNames = ["ApproximateNumberOfMessages"] }))
            .Attributes!["ApproximateNumberOfMessages"];
        Ensure(dlqDepth == "0",
            $"DLQ still reports {dlqDepth} message(s) after a COMPLETED move task");

        Message? redelivered = null;
        deadline = Stopwatch.StartNew();
        while (deadline.Elapsed.TotalSeconds < 40.0 && redelivered is null)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = srcUrl, MaxNumberOfMessages = 1, WaitTimeSeconds = 5 });
            var msgs = resp.Messages ?? [];
            if (msgs.Count > 0)
            {
                redelivered = msgs[0];
            }
        }

        Ensure(redelivered is not null,
            "redriven message never reappeared on the source queue");
        Ensure(redelivered.Body == body, "assert redelivered[\"Body\"] == body");
        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = srcUrl, ReceiptHandle = redelivered.ReceiptHandle });
    }

    /// <summary>Python's <c>json.dumps</c> of a flat dict, with its default separators.</summary>
    private static string PyDumps(params (string Key, object Value)[] items)
        => "{" + string.Join(", ", items.Select(kv => $"{JsonSerializer.Serialize(kv.Key)}: {JsonSerializer.Serialize(kv.Value)}")) + "}";
}

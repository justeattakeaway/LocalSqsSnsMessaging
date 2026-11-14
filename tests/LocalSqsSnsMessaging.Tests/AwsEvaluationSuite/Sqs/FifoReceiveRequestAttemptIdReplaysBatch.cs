// Translated from behavioral/sqs/check_fifo_receive_request_attempt_id_replays_batch.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.fifo-receive-request-attempt-id-replays-batch</summary>
[Category("AwsEvaluationSuite")]
public sealed class FifoReceiveRequestAttemptIdReplaysBatch
{
    /// <summary>
    /// A FIFO ReceiveMessage retried with the same ReceiveRequestAttemptId replays the same batch.
    ///
    /// A different attempt id gets nothing while the group's messages are still in flight.
    /// </summary>
    [Test]
    [DisplayName("sqs.fifo-receive-request-attempt-id-replays-batch")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.Batch, "replay with the same ReceiveRequestAttemptId returned [], expected the original batch")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-") + ".fifo",
            // Long enough that a re-receive under a new attempt id cannot succeed by timeout.
            Attributes = new() { ["FifoQueue"] = "true", ["VisibilityTimeout"] = "30" },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var token = Guid.NewGuid().ToString("N")[..8];
        var group = $"grp-{token}";
        var bodies = Enumerable.Range(0, 3).Select(i => $"rraid-{i}-{token}").ToList();
        for (var i = 0; i < bodies.Count; i++)
        {
            await sqs.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = url,
                MessageBody = bodies[i],
                MessageGroupId = group,
                MessageDeduplicationId = $"{token}-{i}",
            });
        }

        var attempt1 = $"attempt-1-{token}";
        var first = await Poll(
            async () => (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url,
                MaxNumberOfMessages = 3,
                WaitTimeSeconds = 2,
                ReceiveRequestAttemptId = attempt1,
            })).Messages ?? [],
            msgs => msgs.Count > 0,
            timeoutS: 30,
            intervalS: 0);
        Ensure(first.Count > 0, "first ReceiveMessage returned nothing");
        var firstIds = first.Select(m => m.MessageId).ToList();
        var firstBodies = first.Select(m => m.Body).ToList();
        // Whatever prefix arrived, it must be a send-order prefix (asserted as a list).
        Ensure(firstBodies.SequenceEqual(bodies.Take(firstBodies.Count)),
            $"first batch [{string.Join(", ", firstBodies)}] is not a send-order prefix of [{string.Join(", ", bodies)}]");

        // MessageIds, not ReceiptHandles, are compared: the replay may carry fresh handles.
        var replay = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = url,
            MaxNumberOfMessages = 3,
            WaitTimeSeconds = 2,
            ReceiveRequestAttemptId = attempt1,
        })).Messages ?? [];
        var replayIds = replay.Select(m => m.MessageId).ToList();
        Ensure(replayIds.SequenceEqual(firstIds),
            $"replay with the same ReceiveRequestAttemptId returned [{string.Join(", ", replayIds)}], " +
            $"expected the original batch [{string.Join(", ", firstIds)}]");
        Ensure(replay.Select(m => m.Body).SequenceEqual(firstBodies),
            $"replay bodies [{string.Join(", ", replay.Select(m => m.Body))}] != [{string.Join(", ", firstBodies)}]");

        var other = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = url,
            MaxNumberOfMessages = 3,
            WaitTimeSeconds = 2,
            ReceiveRequestAttemptId = $"attempt-2-{token}",
        })).Messages ?? [];
        Ensure(other.Count == 0,
            $"a new ReceiveRequestAttemptId handed out [{string.Join(", ", other.Select(m => m.MessageId))}] while " +
            $"group {group} still had {firstIds.Count} message(s) in flight");

        // Deleting with the latest response's handles releases the rest of the group.
        foreach (var msg in replay)
        {
            await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle });
        }

        var remaining = bodies.Skip(firstBodies.Count).ToList();
        if (remaining.Count > 0)
        {
            var got = await Poll(
                async () => ((await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
                {
                    QueueUrl = url,
                    MaxNumberOfMessages = 3,
                    WaitTimeSeconds = 2,
                    ReceiveRequestAttemptId = $"attempt-3-{token}",
                })).Messages ?? []).Select(m => m.Body).ToList(),
                g => g.Count >= remaining.Count,
                timeoutS: 30,
                intervalS: 0);
            Ensure(got.SequenceEqual(remaining),
                $"after the delete, expected [{string.Join(", ", remaining)}] next, got [{string.Join(", ", got)}]");
        }
    }

    private static async Task<T> Poll<T>(Func<Task<T>> fn, Func<T, bool> pred, double timeoutS, double intervalS = 0.5)
    {
        var deadline = Stopwatch.StartNew();
        var val = await fn();
        while (!pred(val))
        {
            if (deadline.Elapsed.TotalSeconds >= timeoutS)
            {
                return val;
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalS));
            val = await fn();
        }

        return val;
    }
}

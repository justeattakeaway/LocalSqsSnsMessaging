// Translated from behavioral/sqs/check_receive_request_attempt_id_not_retryable_after_batch_modified_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.receive-request-attempt-id-not-retryable-after-batch-modified-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ReceiveRequestAttemptIdNotRetryableAfterBatchModifiedRejected
{
    /// <summary>
    /// A FIFO retry with a ReceiveRequestAttemptId replays the batch until one of its messages is
    /// deleted or has its visibility changed; then it is refused as not retryable and hands out
    /// nothing, leaving the group's untouched in-flight messages locked.
    /// </summary>
    [Test]
    [DisplayName("sqs.receive-request-attempt-id-not-retryable-after-batch-modified-rejected")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.Batch, "Check timed out after 60.0s")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var token = Guid.NewGuid().ToString("N")[..8];
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-") + ".fifo",
            Attributes = new() { ["FifoQueue"] = "true", ["VisibilityTimeout"] = "120" },
            Tags = new() { ["sim2real:managed"] = "true", ["sim2real"] = "rraid-not-retryable" },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var bodies = Enumerable.Range(0, 3).Select(i => $"m{i}-{token}").ToList();
        for (var i = 0; i < bodies.Count; i++)
        {
            await sqs.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = url,
                MessageBody = bodies[i],
                MessageGroupId = "g",
                MessageDeduplicationId = $"{token}-{i}",
            });
        }

        // Wait until all three are on the queue, so one receive can take them as one batch.
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(60) && (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
               {
                   QueueUrl = url,
                   AttributeNames = ["ApproximateNumberOfMessages"],
               })).Attributes!["ApproximateNumberOfMessages"] != "3")
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        // --- leg 1: modification by DeleteMessage ------------------------------------------
        var attemptA = $"attempt-a-{token}";
        List<Message> batch = [];
        deadline = Stopwatch.StartNew();
        while (batch.Count == 0 && deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            batch = await Receive(sqs, url, attemptA);
        }

        Ensure(batch.Select(m => m.Body).SequenceEqual(bodies.Take(batch.Count)) && batch.Count >= 2,
            $"expected a send-order batch of at least two of [{string.Join(", ", bodies)}], got "
            + $"[{string.Join(", ", batch.Select(m => m.Body))}]");
        var replay = await Receive(sqs, url, attemptA);
        Ensure(replay.Select(m => m.MessageId).SequenceEqual(batch.Select(m => m.MessageId)),
            "positive control: an unmodified batch must replay under its attempt id, got "
            + $"[{string.Join(", ", replay.Select(m => m.Body))}]");
        var victim = replay[0];
        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = victim.ReceiptHandle });

        var (code, msg) = await Error(() => Receive(sqs, url, attemptA));
        Ensure((code, msg) == ("InvalidParameterValue", NotRetryable(attemptA)),
            $"retrying a batch after one of its messages was deleted gave ({code}, {msg})");

        // Nothing was handed out by the refusal: the group is still locked by its in-flight messages.
        var leaked = await Receive(sqs, url, $"probe-{token}");
        Ensure(leaked.Count == 0,
            "after the refused retry the group's in-flight messages must stay hidden: "
            + $"[{string.Join(", ", leaked.Select(m => m.Body))}]");

        // --- leg 2: modification by ChangeMessageVisibility --------------------------------
        foreach (var m in replay.Skip(1))
        {
            await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest
            {
                QueueUrl = url,
                ReceiptHandle = m.ReceiptHandle,
                VisibilityTimeout = 0,
            });
        }

        var attemptB = $"attempt-b-{token}";
        List<Message> rest = [];
        deadline = Stopwatch.StartNew();
        while (rest.Count == 0 && deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            rest = await Receive(sqs, url, attemptB);
        }

        var remaining = bodies.Where(b => b != victim.Body).ToList();
        Ensure(rest.Select(m => m.Body).SequenceEqual(remaining.Take(rest.Count)) && rest.Count > 0,
            "after releasing the in-flight messages the group must resume in order at "
            + $"[{string.Join(", ", remaining)}], got [{string.Join(", ", rest.Select(m => m.Body))}]");
        await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest
        {
            QueueUrl = url,
            ReceiptHandle = rest[0].ReceiptHandle,
            VisibilityTimeout = 60,
        });
        (code, msg) = await Error(() => Receive(sqs, url, attemptB));
        Ensure((code, msg) == ("InvalidParameterValue", NotRetryable(attemptB)),
            "retrying a batch after one of its messages had its visibility changed gave "
            + $"({code}, {msg})");
    }

    /// <summary>(code, message) of the refusal <paramref name="fn"/> raises; an unexpected success raises, naming it.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task<List<Message>>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result!.Count} message(s)");
        }

        return (error.Code, error.Message);
    }

    private static string NotRetryable(string attempt) =>
        $"Value {attempt} for parameter ReceiveRequestAttemptId is invalid. Reason: The "
        + "receiveRequestAttemptId is not valid anymore since the receive call is not "
        + "retryable.";

    private static async Task<List<Message>> Receive(IAmazonSQS sqs, string url, string attempt, int waitS = 2) =>
        (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = url,
            MaxNumberOfMessages = 10,
            WaitTimeSeconds = waitS,
            ReceiveRequestAttemptId = attempt,
        })).Messages ?? [];
}

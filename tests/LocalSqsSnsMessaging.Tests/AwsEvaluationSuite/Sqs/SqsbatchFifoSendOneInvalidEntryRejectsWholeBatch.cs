// Translated from behavioral/sqs/check_sqsbatch_fifo_send_one_invalid_entry_rejects_whole_batch.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.sqsbatch-fifo-send-one-invalid-entry-rejects-whole-batch</summary>
[Category("AwsEvaluationSuite")]
public sealed class SqsbatchFifoSendOneInvalidEntryRejectsWholeBatch
{
    // Top-level Error.Code real AWS raises when a FIFO SendMessageBatch carries a malformed entry.
    private const string ErrInvalidParameterValue = "InvalidParameterValue";
    // Per-entry BatchResultErrorEntry.Code the SAME malformation produces on a standard queue.
    private const string ErrEmptyValue = "EmptyValue";

    private const int BatchMaxEntries = 10;
    private const int BadIndex = 4;

    /// <summary>
    /// One empty-bodied entry rejects a whole FIFO SendMessageBatch (InvalidParameterValue, nothing
    /// enqueued), while on a standard queue it is one Failed entry (EmptyValue) and nine siblings land.
    /// </summary>
    [Test]
    [DisplayName("sqs.sqsbatch-fifo-send-one-invalid-entry-rejects-whole-batch")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.Batch, "FIFO SendMessageBatch with one empty-bodied entry unexpectedly SUCCEEDED; real AWS rejects the whole request with InvalidParameterValue")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var fifoUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-") + ".fifo",
            Attributes = new() { ["FifoQueue"] = "true", ["ContentBasedDeduplication"] = "false", ["VisibilityTimeout"] = "30" },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(fifoUrl));
        var stdUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-"),
            Attributes = new() { ["VisibilityTimeout"] = "30" },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(stdUrl));

        var group = "sim2real-group-" + Guid.NewGuid().ToString("N")[..8];
        const int observeS = 14;
        const int absentS = 14;

        List<SendMessageBatchRequestEntry> FifoEntries(List<string> bodies)
        {
            // A fresh MessageDeduplicationId per entry per call: dedup must never be what makes a
            // sibling invisible, or the effect-absent assertion below would prove nothing.
            return [.. Enumerable.Range(0, BatchMaxEntries).Select(i => new SendMessageBatchRequestEntry
            {
                Id = $"e{i}",
                MessageBody = bodies[i],
                MessageGroupId = group,
                MessageDeduplicationId = Guid.NewGuid().ToString("N"),
            })];
        }

        // Positive control: a wholly valid full-width FIFO batch is accepted in full.
        var ctlBodies = Enumerable.Range(0, BatchMaxEntries).Select(i => $"fifo-control-{i}").ToList();
        var control = await sqs.SendMessageBatchAsync(new SendMessageBatchRequest { QueueUrl = fifoUrl, Entries = FifoEntries(ctlBodies) });
        var ctlOk = (control.Successful ?? []).Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        Ensure(ctlOk.SetEquals(Enumerable.Range(0, BatchMaxEntries).Select(i => $"e{i}")),
            $"valid FIFO batch Successful Ids {Sorted(ctlOk)}, expected all ten");
        Ensure(control.Failed is null or { Count: 0 }, $"valid FIFO batch reported {control.Failed?.Count} failures");
        foreach (var entry in control.Successful!)
        {
            Ensure(!string.IsNullOrEmpty(entry.SequenceNumber),
                $"FIFO Successful entry '{entry.Id}' carries no SequenceNumber");
        }

        // Within one message group FIFO order is guaranteed, so arrival order is assertable here.
        var ctlOrder = await Observe(sqs, fifoUrl, timeoutSeconds: observeS);
        Ensure(ctlOrder.SequenceEqual(ctlBodies),
            $"FIFO group '{group}' delivered [{string.Join(", ", ctlOrder)}], expected send order [{string.Join(", ", ctlBodies)}]");

        // Break exactly one thing: entry 4 of 10 carries an empty MessageBody.
        var bodies = Enumerable.Range(0, BatchMaxEntries).Select(i => i == BadIndex ? "" : $"fifo-must-not-enqueue-{i}").ToList();
        var siblings = bodies.Where((b, i) => i != BadIndex).ToList();

        var (code, message) = await ErrorCode(() => sqs.SendMessageBatchAsync(new SendMessageBatchRequest { QueueUrl = fifoUrl, Entries = FifoEntries(bodies) }));
        Ensure(code == ErrInvalidParameterValue,
            $"FIFO SendMessageBatch with one empty-bodied entry raised '{code}', expected a " +
            $"whole-request '{ErrInvalidParameterValue}'");
        Ensure(message == $"No Value Found for SendMessageBatchRequestEntry.{BadIndex + 1}.MessageBody.",
            $"FIFO SendMessageBatch refusal message is '{message}'");

        // Effect absent: not one of the nine valid siblings reached the FIFO queue.
        var leaked = await Observe(sqs, fifoUrl, timeoutSeconds: absentS);
        Ensure(leaked.Count == 0,
            $"the rejected FIFO batch enqueued [{string.Join(", ", leaked)}]; a whole-request rejection must enqueue " +
            $"nothing, and every one of [{string.Join(", ", siblings)}] was individually valid");
        var depth = await Poll(
            async () => (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = fifoUrl,
                AttributeNames = ["ApproximateNumberOfMessages", "ApproximateNumberOfMessagesNotVisible"],
            })).Attributes!,
            a => a["ApproximateNumberOfMessages"] == "0" && a["ApproximateNumberOfMessagesNotVisible"] == "0",
            timeoutSeconds: 30);
        Ensure(depth["ApproximateNumberOfMessages"] == "0",
            $"FIFO queue reports '{depth["ApproximateNumberOfMessages"]}' visible messages after a " +
            "wholly rejected batch");
        Ensure(depth["ApproximateNumberOfMessagesNotVisible"] == "0",
            $"FIFO queue reports '{depth["ApproximateNumberOfMessagesNotVisible"]}' in flight after " +
            "a wholly rejected batch");

        // Contrast: the same malformation on a standard queue fails per entry.
        var stdBodies = Enumerable.Range(0, BatchMaxEntries).Select(i => i == BadIndex ? "" : $"std-keeps-{i}").ToList();
        var stdSiblings = stdBodies.Where((b, i) => i != BadIndex).ToHashSet(StringComparer.Ordinal);
        var stdResp = await sqs.SendMessageBatchAsync(new SendMessageBatchRequest
        {
            QueueUrl = stdUrl,
            Entries = [.. Enumerable.Range(0, BatchMaxEntries).Select(i => new SendMessageBatchRequestEntry { Id = $"e{i}", MessageBody = stdBodies[i] })],
        });
        var stdOk = (stdResp.Successful ?? []).Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        var stdFail = (stdResp.Failed ?? []).ToDictionary(e => e.Id, StringComparer.Ordinal);
        Ensure(stdOk.SetEquals(Enumerable.Range(0, BatchMaxEntries).Where(i => i != BadIndex).Select(i => $"e{i}")),
            $"standard-queue Successful Ids {Sorted(stdOk)} != the nine valid entries");
        Ensure(stdFail.Keys.ToHashSet().SetEquals([$"e{BadIndex}"]),
            $"standard-queue Failed Ids {Sorted(stdFail.Keys)}, expected exactly ['e{BadIndex}']");
        Ensure(stdFail[$"e{BadIndex}"].Code == ErrEmptyValue,
            $"standard-queue Failed Code is '{stdFail[$"e{BadIndex}"].Code}', expected " +
            $"'{ErrEmptyValue}' -- a per-entry code, not the whole-request " +
            $"'{ErrInvalidParameterValue}' FIFO gives");
        Ensure(stdFail[$"e{BadIndex}"].Message == $"No Value Found for SendMessageBatchRequestEntry.{BadIndex + 1}.MessageBody.",
            $"standard-queue Failed Message is '{stdFail[$"e{BadIndex}"].Message}'");
        Ensure(stdFail[$"e{BadIndex}"].SenderFault == true,
            $"standard-queue Failed SenderFault is {stdFail[$"e{BadIndex}"].SenderFault}, expected True");
        var stdGot = await Observe(sqs, stdUrl, timeoutSeconds: observeS);
        Ensure(stdGot.ToHashSet(StringComparer.Ordinal).SetEquals(stdSiblings),
            $"standard queue delivered {Sorted(stdGot)}, expected exactly the nine valid bodies " +
            $"{Sorted(stdSiblings)} -- the per-entry model must enqueue the siblings that FIFO " +
            "refuses");
    }

    private static string Sorted(IEnumerable<string> ids) => "[" + string.Join(", ", ids.Order(StringComparer.Ordinal).Select(i => $"'{i}'")) + "]";

    /// <summary>Return the botocore (Error.Code, Error.Message) raised by fn(); raise if it succeeds.</summary>
    private static async Task<(string? Code, string? Message)> ErrorCode(Func<Task<SendMessageBatchResponse>> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is not null)
        {
            Ensure(!string.IsNullOrEmpty(error.Code), $"raised {error.Exception.GetType().Name} with no Error.Code: {error.Exception.Message}");
            return (error.Code, error.Message);
        }

        throw Fail("call unexpectedly SUCCEEDED; real AWS rejects this request");
    }

    private static async Task<T> Poll<T>(Func<Task<T>> fn, Func<T, bool> pred, double timeoutSeconds, double intervalSeconds = 0.5)
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

    /// <summary>
    /// Bodies of every distinct message visible within timeout_s, in arrival order, each deleted.
    ///
    /// The full window always runs, so an empty result is effect-absent evidence.
    /// </summary>
    private static async Task<List<string>> Observe(AmazonSQSClient sqs, string url, double timeoutSeconds)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var bodies = new List<string>();
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 1 });
            foreach (var msg in resp.Messages ?? [])
            {
                if (seen.Add(msg.MessageId))
                {
                    bodies.Add(msg.Body);
                }

                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle });
            }
        }

        return bodies;
    }
}

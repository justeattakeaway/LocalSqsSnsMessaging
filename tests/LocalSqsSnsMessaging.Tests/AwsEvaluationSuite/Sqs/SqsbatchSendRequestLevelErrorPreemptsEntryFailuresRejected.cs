// Translated from behavioral/sqs/check_sqsbatch_send_request_level_error_preempts_entry_failures_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.sqsbatch-send-request-level-error-preempts-entry-failures-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class SqsbatchSendRequestLevelErrorPreemptsEntryFailuresRejected
{
    // Request-level code, in SQS's AWS.SimpleQueueService namespace, not the bare shape name.
    private const string ErrIdsNotDistinct = "AWS.SimpleQueueService.BatchEntryIdsNotDistinct";
    // The per-entry code the same empty body earns once the request-level fault is repaired.
    private const string ErrEmptyValue = "EmptyValue";

    private const int BatchMaxEntries = 10;
    // The entry whose MessageBody is empty (a per-entry fault) and the entry whose Id is a repeat
    // of entry 2's (a request-level fault). Two different entries, so neither masks the other.
    private const int BadBodyIndex = 4;
    private const int DupIdIndex = 7;
    private const int DupOfIndex = 2;

    /// <summary>
    /// A batch with a repeated entry Id and an empty body is rejected wholesale with
    /// BatchEntryIdsNotDistinct and enqueues nothing; with only the Id repaired the same batch
    /// splits per entry, with EmptyValue on the empty-bodied entry.
    ///
    /// Reference: https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_SendMessageBatch.html
    /// </summary>
    [Test]
    [DisplayName("sqs.sqsbatch-send-request-level-error-preempts-entry-failures-rejected")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.Batch, "SendMessageBatch with a repeated entry Id unexpectedly SUCCEEDED (the .NET SDK then throws ArgumentException validating the response); real AWS rejects it with BatchEntryIdsNotDistinct")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        const int observeS = 14;

        var bodies = Enumerable.Range(0, BatchMaxEntries).Select(i => $"preempt-body-{i}").ToList();
        var distinctIds = Enumerable.Range(0, BatchMaxEntries).Select(i => $"e{i}").ToList();
        var repeatedIds = distinctIds.ToList();
        repeatedIds[DupIdIndex] = distinctIds[DupOfIndex];

        List<SendMessageBatchRequestEntry> Entries(List<string> ids, int emptyBodyAt)
            => [.. Enumerable.Range(0, BatchMaxEntries).Select(i => new SendMessageBatchRequestEntry
            {
                Id = ids[i],
                MessageBody = i == emptyBodyAt ? "" : bodies[i],
            })];

        // Both faults at once: the request-level one must win.
        var innocent = Enumerable.Range(0, BatchMaxEntries).Where(i => i is not (BadBodyIndex or DupIdIndex)).Select(i => bodies[i]).ToList();
        var (code, message) = await ErrorCode(() => sqs.SendMessageBatchAsync(new SendMessageBatchRequest
        {
            QueueUrl = url,
            Entries = Entries(repeatedIds, emptyBodyAt: BadBodyIndex),
        }));
        Ensure(code == ErrIdsNotDistinct,
            $"a batch with a repeated Id AND an empty body raised '{code}'; the request-level " +
            $"fault must win with '{ErrIdsNotDistinct}' rather than degrading to a per-entry " +
            $"'{ErrEmptyValue}' split");
        Ensure(message == $"Id {distinctIds[DupOfIndex]} repeated.", $"the request-level refusal message is '{message}'");

        var leaked = await Observe(sqs, url, timeoutSeconds: observeS);
        Ensure(leaked.Count == 0,
            $"the wholly rejected batch enqueued [{string.Join(", ", leaked)}]; all of [{string.Join(", ", innocent)}] were individually " +
            "valid with distinct Ids and still must not land");
        var depth = await Poll(
            async () => (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = url,
                AttributeNames = ["ApproximateNumberOfMessages", "ApproximateNumberOfMessagesNotVisible"],
            })).Attributes!,
            a => a["ApproximateNumberOfMessages"] == "0" && a["ApproximateNumberOfMessagesNotVisible"] == "0",
            timeoutSeconds: 30);
        Ensure(depth["ApproximateNumberOfMessages"] == "0",
            $"queue reports '{depth["ApproximateNumberOfMessages"]}' visible messages after a " +
            "wholly rejected batch");
        Ensure(depth["ApproximateNumberOfMessagesNotVisible"] == "0",
            $"queue reports '{depth["ApproximateNumberOfMessagesNotVisible"]}' in flight after a " +
            "wholly rejected batch");

        // Positive control: repair only the repeated Id.
        var resp = await sqs.SendMessageBatchAsync(new SendMessageBatchRequest
        {
            QueueUrl = url,
            Entries = Entries(distinctIds, emptyBodyAt: BadBodyIndex),
        });
        var succ = (resp.Successful ?? []).Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        var fail = (resp.Failed ?? []).ToDictionary(e => e.Id, StringComparer.Ordinal);
        var wantOk = Enumerable.Range(0, BatchMaxEntries).Where(i => i != BadBodyIndex).Select(i => distinctIds[i]).ToHashSet(StringComparer.Ordinal);
        Ensure(succ.SetEquals(wantOk),
            $"with distinct Ids the Successful list is {Sorted(succ)}, expected the nine " +
            $"non-empty entries {Sorted(wantOk)}");
        Ensure(fail.Keys.ToHashSet().SetEquals([distinctIds[BadBodyIndex]]),
            $"Failed Ids {Sorted(fail.Keys)}, expected exactly ['{distinctIds[BadBodyIndex]}']");
        Ensure(fail[distinctIds[BadBodyIndex]].Code == ErrEmptyValue,
            $"per-entry Code is '{fail[distinctIds[BadBodyIndex]].Code}', expected " +
            $"'{ErrEmptyValue}'; this is the fault the request-level error suppressed above");
        Ensure(fail[distinctIds[BadBodyIndex]].Message == $"No Value Found for SendMessageBatchRequestEntry.{BadBodyIndex + 1}.MessageBody.",
            $"per-entry Message is '{fail[distinctIds[BadBodyIndex]].Message}'");
        var wantBodies = Enumerable.Range(0, BatchMaxEntries).Where(i => i != BadBodyIndex).Select(i => bodies[i]).ToHashSet(StringComparer.Ordinal);
        var got = await Observe(sqs, url, timeoutSeconds: observeS);
        Ensure(got.ToHashSet(StringComparer.Ordinal).SetEquals(wantBodies),
            $"the repaired batch delivered {Sorted(got)}, expected the nine valid bodies " +
            $"{Sorted(wantBodies)}");
    }

    private static string Sorted(IEnumerable<string> ids) => "[" + string.Join(", ", ids.Order(StringComparer.Ordinal).Select(i => $"'{i}'")) + "]";

    /// <summary>Return the botocore (Error.Code, Error.Message) raised by fn(); fail if fn() succeeds.</summary>
    private static async Task<(string? Code, string? Message)> ErrorCode(Func<Task<SendMessageBatchResponse>> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is not null)
        {
            Ensure(!string.IsNullOrEmpty(error.Code), $"raised {error.Exception.GetType().Name} with no Error.Code: {error.Exception.Message}");
            return (error.Code, error.Message);
        }

        throw Fail("SendMessageBatch unexpectedly SUCCEEDED; real AWS rejects this request");
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
    /// Bodies of every distinct message that becomes visible inside timeout_s, each deleted so
    /// it is counted once. Always runs the full window: the window closing with nothing further
    /// arriving is the effect-absent evidence.
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

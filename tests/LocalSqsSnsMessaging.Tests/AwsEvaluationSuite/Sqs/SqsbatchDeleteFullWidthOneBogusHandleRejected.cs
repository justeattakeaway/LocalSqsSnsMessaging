// Translated from behavioral/sqs/check_sqsbatch_delete_full_width_one_bogus_handle_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using System.Globalization;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.sqsbatch-delete-full-width-one-bogus-handle-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class SqsbatchDeleteFullWidthOneBogusHandleRejected
{
    private const string Check = "sqsbatch-delete-full-width-one-bogus-handle-rejected";

    // Per-entry BatchResultErrorEntry.Code real AWS returns for a syntactically bogus handle.
    private const string ErrReceiptHandleInvalid = "ReceiptHandleIsInvalid";

    // DeleteMessageBatch's fixed API maximum. A fixed API constant, not a Service-Quotas value.
    private const int BatchMaxEntries = 10;

    // Which of the ten entries gets the bogus handle. Neither first nor last, so a target that
    // aborts at the first bad entry and one that ignores the tail both diverge.
    private const int BadIndex = 7;

    // Long enough to receive all ten before any handle expires, short enough that the one
    // surviving message redelivers inside the check. Read back off the queue rather than assumed.
    private const int VisibilityTimeoutSeconds = 10;

    private const string BogusHandle = "AQEBsim2real-not-a-real-receipt-handle-0000000000";

    private static Dictionary<string, string> Tags() => new() { ["sim2real:managed"] = "true", ["sim2real"] = Check };

    /// <summary>
    /// A 10-entry DeleteMessageBatch with one bogus receipt handle deletes the other nine: Failed
    /// names only that Id (ReceiptHandleIsInvalid, SenderFault=true) and only its message redelivers.
    ///
    /// Reference: https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_DeleteMessageBatch.html
    /// </summary>
    // timing:real-time — visibility: 10 s https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/sqs-visibility-timeout.html
    [Test]
    [DisplayName("sqs.sqsbatch-delete-full-width-one-bogus-handle-rejected")]
    [Timeout(FidelityContext.RealTimeBudgetMilliseconds)]
    [Category(ApplyDefaultCategoryAttribute.TimeBasedCategoryName)]
    [KnownFidelityGap(FidelityGap.ErrorMessageText, "Failed['d7'].Message is 'Receipt handle AQEBsim2real-not-a-real-receipt-handle-0000000000 is invalid.', expected 'The input receipt handle is invalid.'")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.RealTimeBudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-"),
            Attributes = new() { ["VisibilityTimeout"] = VisibilityTimeoutSeconds.ToString(CultureInfo.InvariantCulture) },
            Tags = Tags(),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var vis = int.Parse((await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = url,
            AttributeNames = ["VisibilityTimeout"],
        })).Attributes!["VisibilityTimeout"], CultureInfo.InvariantCulture);
        Ensure(vis == VisibilityTimeoutSeconds,
            $"queue reports VisibilityTimeout={vis}, expected {VisibilityTimeoutSeconds}; the " +
            "redelivery window below is derived from this value");
        const int recvS = 12;
        // Windows derived from the queue's reported timeout: `empty_s` outlasts one timeout;
        // `redeliver_s` also leaves room for the redelivery itself, so it doubles it.
        var emptyS = vis + 5;
        var redeliverS = vis * 2;

        async Task<List<string>> SendTen(string prefix)
        {
            var want = Enumerable.Range(0, BatchMaxEntries).Select(i => $"{prefix}-{i}").ToList();
            var resp = await sqs.SendMessageBatchAsync(new SendMessageBatchRequest
            {
                QueueUrl = url,
                Entries = [.. Enumerable.Range(0, BatchMaxEntries).Select(i => new SendMessageBatchRequestEntry { Id = $"s{i}", MessageBody = want[i] })],
            });
            Ensure(resp.Failed is null or { Count: 0 }, $"setup send batch '{prefix}' failed: {resp.Failed?.Count}");
            Ensure((resp.Successful ?? []).Count == BatchMaxEntries,
                $"setup send batch '{prefix}' enqueued {(resp.Successful ?? []).Count} of {BatchMaxEntries}");
            return want;
        }

        // --- positive control: all ten handles valid -> ten Successful, queue empties -----------
        var ctlBodies = await SendTen("control");
        var ctlHandles = await ReceiveByBody(sqs, url, want: ctlBodies, timeoutSeconds: recvS);
        Ensure(ctlHandles.Keys.ToHashSet().SetEquals(ctlBodies),
            $"control receive got {Sorted(ctlHandles.Keys)} of {Sorted(ctlBodies)} within {recvS}s");
        var ctlResp = await sqs.DeleteMessageBatchAsync(new DeleteMessageBatchRequest
        {
            QueueUrl = url,
            Entries = [.. Enumerable.Range(0, BatchMaxEntries).Select(i => new DeleteMessageBatchRequestEntry { Id = $"c{i}", ReceiptHandle = ctlHandles[ctlBodies[i]] })],
        });
        var ctlOk = (ctlResp.Successful ?? []).Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        Ensure(ctlOk.SetEquals(Enumerable.Range(0, BatchMaxEntries).Select(i => $"c{i}")),
            $"control DeleteMessageBatch Successful Ids {Sorted(ctlOk)}, expected all ten");
        Ensure(ctlResp.Failed is null or { Count: 0 }, $"control delete reported {ctlResp.Failed?.Count} failures");
        var stragglers = await Observe(sqs, url, timeoutSeconds: emptyS);
        Ensure(stragglers.Count == 0,
            $"after a wholly successful full-width delete the queue redelivered {Sorted(stragglers)}; " +
            "the Successful entries were a response-only claim");

        // --- one entry (BAD_INDEX) carries a bogus receipt handle -------------------------------
        var bodies = await SendTen("round2");
        var handles = await ReceiveByBody(sqs, url, want: bodies, timeoutSeconds: recvS);
        Ensure(handles.Keys.ToHashSet().SetEquals(bodies),
            $"receive got {Sorted(handles.Keys)} of {Sorted(bodies)} within {recvS}s; all ten must be " +
            "in flight before the batch delete");
        var survivor = bodies[BadIndex];
        var deletedBodies = Enumerable.Range(0, BatchMaxEntries).Where(i => i != BadIndex).Select(i => bodies[i]).ToHashSet(StringComparer.Ordinal);
        var badId = $"d{BadIndex}";
        var entries = Enumerable.Range(0, BatchMaxEntries)
            .Select(i => new DeleteMessageBatchRequestEntry
            {
                Id = $"d{i}",
                ReceiptHandle = i == BadIndex ? BogusHandle : handles[bodies[i]],
            })
            .ToList();

        var resp = await sqs.DeleteMessageBatchAsync(new DeleteMessageBatchRequest { QueueUrl = url, Entries = entries });
        var succ = (resp.Successful ?? []).Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        var fail = (resp.Failed ?? []).ToDictionary(e => e.Id, StringComparer.Ordinal);

        Ensure(succ.SetEquals(Enumerable.Range(0, BatchMaxEntries).Where(i => i != BadIndex).Select(i => $"d{i}")),
            $"Successful Ids {Sorted(succ)} != the nine entries with valid handles");
        Ensure(fail.Keys.ToHashSet().SetEquals([badId]), $"Failed Ids {Sorted(fail.Keys)}, expected exactly ['{badId}']");
        Ensure(succ.Union(fail.Keys).ToHashSet().SetEquals(Enumerable.Range(0, BatchMaxEntries).Select(i => $"d{i}")),
            $"envelope Ids {Sorted(succ.Union(fail.Keys))} do not partition the ten request Ids");

        var bad = fail[badId];
        Ensure(bad.Code == ErrReceiptHandleInvalid,
            $"Failed['{badId}'].Code is '{bad.Code}', expected '{ErrReceiptHandleInvalid}'");
        Ensure(bad.SenderFault == true,
            $"Failed['{badId}'].SenderFault must be True for a malformed handle, got {bad.SenderFault}");
        Ensure(bad.Message == "The input receipt handle is invalid.",
            $"Failed['{badId}'].Message is '{bad.Message}'");

        // --- effect and effect-absent, read off the queue rather than the response -------------
        // Only the broken entry's message may come back. If any of the nine reappears, its
        // Successful entry did not really delete it; if NOTHING comes back, the Failed entry
        // deleted a message it reported it had refused.
        var returned = await Observe(sqs, url, timeoutSeconds: redeliverS);
        Ensure(returned.SequenceEqual([survivor]),
            $"after the visibility timeout lapsed the queue redelivered [{string.Join(", ", returned)}], expected " +
            $"exactly ['{survivor}'] -- the nine Successful entries must be gone and the one " +
            "Failed entry's message must still be there");
        Ensure(!returned.ToHashSet().Overlaps(deletedBodies),
            $"a message reported Successful came back: {Sorted(returned.Intersect(deletedBodies))}");

        var attrs = await Poll(
            async () => (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = url,
                AttributeNames = ["ApproximateNumberOfMessages", "ApproximateNumberOfMessagesNotVisible"],
            })).Attributes!,
            a => a["ApproximateNumberOfMessages"] == "0" && a["ApproximateNumberOfMessagesNotVisible"] == "0",
            timeoutSeconds: vis * 3);
        Ensure(attrs["ApproximateNumberOfMessages"] == "0",
            $"queue still reports '{attrs["ApproximateNumberOfMessages"]}' visible messages after " +
            "the survivor was drained");
        Ensure(attrs["ApproximateNumberOfMessagesNotVisible"] == "0",
            $"queue still reports '{attrs["ApproximateNumberOfMessagesNotVisible"]}' in flight " +
            "after every message was deleted");
    }

    private static string Sorted(IEnumerable<string> ids) => "[" + string.Join(", ", ids.Order(StringComparer.Ordinal).Select(i => $"'{i}'")) + "]";

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
    /// Receive until every body in <paramref name="want"/> has been seen once; return {body: ReceiptHandle}.
    ///
    /// Does not delete: the caller owns the handles. Returns whatever it has when the window
    /// closes, so the caller's assert names the shortfall.
    /// </summary>
    private static async Task<Dictionary<string, string>> ReceiveByBody(AmazonSQSClient sqs, string url, List<string> want, double timeoutSeconds)
    {
        var handles = new Dictionary<string, string>(StringComparer.Ordinal);
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutSeconds) && handles.Count < want.Count)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 1 });
            foreach (var msg in resp.Messages ?? [])
            {
                handles.TryAdd(msg.Body, msg.ReceiptHandle);
            }
        }

        return handles;
    }

    /// <summary>
    /// Collect the bodies of every distinct message that becomes visible inside timeout_s,
    /// deleting each so it is counted once. The full window is always run: the window closing
    /// with nothing further arriving is the effect-absent evidence.
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

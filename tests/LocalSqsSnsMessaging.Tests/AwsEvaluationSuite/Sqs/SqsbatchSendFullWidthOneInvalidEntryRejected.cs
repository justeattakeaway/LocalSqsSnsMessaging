// Translated from behavioral/sqs/check_sqsbatch_send_full_width_one_invalid_entry_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.sqsbatch-send-full-width-one-invalid-entry-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class SqsbatchSendFullWidthOneInvalidEntryRejected
{
    // Per-entry BatchResultErrorEntry.Code real AWS returns for an absent/empty MessageBody.
    private const string ErrEmptyValue = "EmptyValue";

    // SendMessageBatch's fixed API maximum, not an adjustable quota.
    private const int BatchMaxEntries = 10;

    // Deliberately neither first nor last, so short-circuiting and tail truncation both show.
    private const int BadIndex = 4;

    // Entry Ids are [a-zA-Z0-9_-]{1,80}; mixed case and non-positional so they must be echoed exactly.
    private static readonly string[] Ids =
    [
        "Entry_00-aLPHa", "Entry_01-bRAVO", "Entry_02-cHARLIE", "Entry_03-dELTA",
        "Entry_04-eCHO", "Entry_05-fOXTROT", "Entry_06-gOLF", "Entry_07-hOTEL",
        "Entry_08-iNDIA", "Entry_09-jULIETT",
    ];

    /// <summary>
    /// A 10-entry SendMessageBatch with one empty body splits 9/1: Successful echoes the nine Ids
    /// with each body's MD5, Failed names only the bad Id (EmptyValue, SenderFault), and exactly the
    /// nine good bodies are receivable afterwards.
    ///
    /// Reference: https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_SendMessageBatch.html
    /// </summary>
    [Test]
    [DisplayName("sqs.sqsbatch-send-full-width-one-invalid-entry-rejected")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        const int drainS = 14;

        Ensure(Ids.Length == BatchMaxEntries, $"fixture bug: {Ids.Length} ids, want {BatchMaxEntries}");

        // --- positive control: a wholly valid FULL-WIDTH batch reports no failures at all ------
        var ctlBodies = Enumerable.Range(0, BatchMaxEntries).ToDictionary(i => i, i => $"control-body-{i}");
        var control = await sqs.SendMessageBatchAsync(new SendMessageBatchRequest
        {
            QueueUrl = url,
            Entries = [.. Enumerable.Range(0, BatchMaxEntries).Select(i => new SendMessageBatchRequestEntry { Id = Ids[i], MessageBody = ctlBodies[i] })],
        });
        var ctlOk = (control.Successful ?? []).Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        Ensure(ctlOk.SetEquals(Ids),
            $"a wholly valid {BatchMaxEntries}-entry batch reported Successful Ids " +
            $"{Sorted(ctlOk)}, expected all of {Sorted(Ids)}");
        Ensure(control.Failed is null or { Count: 0 }, $"control batch reported failures: {control.Failed?.Count}");
        // A standard queue is unordered, so the drained bodies are compared as a set.
        var drainedCtl = await Drain(sqs, url, timeoutSeconds: drainS);
        Ensure(drainedCtl.ToHashSet(StringComparer.Ordinal).SetEquals(ctlBodies.Values),
            $"control drain returned {Sorted(drainedCtl)}, expected {Sorted(ctlBodies.Values)}");

        // --- break exactly one thing: entry 4 of 10 carries an empty MessageBody ---------------
        var bodies = Enumerable.Range(0, BatchMaxEntries).ToDictionary(i => i, i => $"kept-body-{i}");
        var entries = Enumerable.Range(0, BatchMaxEntries)
            .Select(i => new SendMessageBatchRequestEntry { Id = Ids[i], MessageBody = i == BadIndex ? "" : bodies[i] })
            .ToList();
        var badId = Ids[BadIndex];
        var goodIds = Enumerable.Range(0, BatchMaxEntries).Where(i => i != BadIndex).Select(i => Ids[i]).ToHashSet(StringComparer.Ordinal);
        var goodBodies = Enumerable.Range(0, BatchMaxEntries).Where(i => i != BadIndex).Select(i => bodies[i]).ToHashSet(StringComparer.Ordinal);

        var resp = await sqs.SendMessageBatchAsync(new SendMessageBatchRequest { QueueUrl = url, Entries = entries });
        var succ = (resp.Successful ?? []).ToDictionary(e => e.Id, StringComparer.Ordinal);
        var fail = (resp.Failed ?? []).ToDictionary(e => e.Id, StringComparer.Ordinal);

        Ensure(succ.Keys.ToHashSet().SetEquals(goodIds),
            $"Successful Ids {Sorted(succ.Keys)} != the nine good request Ids {Sorted(goodIds)}");
        Ensure(fail.Keys.ToHashSet().SetEquals([badId]), $"Failed Ids {Sorted(fail.Keys)}, expected exactly ['{badId}']");
        Ensure(succ.Keys.Union(fail.Keys).ToHashSet().SetEquals(Ids),
            $"envelope Ids {Sorted(succ.Keys.Union(fail.Keys))} do not partition the request Ids " +
            $"{Sorted(Ids)}");

        var bad = fail[badId];
        Ensure(bad.Code == ErrEmptyValue, $"Failed['{badId}'].Code is '{bad.Code}', expected '{ErrEmptyValue}'");
        Ensure(bad.SenderFault == true,
            $"Failed['{badId}'].SenderFault must be True for a client error (a retry can never " +
            $"succeed), got {bad.SenderFault}");
        Ensure(bad.Message == $"No Value Found for SendMessageBatchRequestEntry.{BadIndex + 1}.MessageBody.",
            $"Failed['{badId}'].Message is '{bad.Message}'");

        // Each Successful entry's digest must be of the body THAT Id carried.
        for (var i = 0; i < BatchMaxEntries; i++)
        {
            if (i == BadIndex)
            {
                continue;
            }

            var entry = succ[Ids[i]];
            var want = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(bodies[i])));
            Ensure(entry.MD5OfMessageBody == want,
                $"Successful['{Ids[i]}'].MD5OfMessageBody is '{entry.MD5OfMessageBody}', " +
                $"expected md5('{bodies[i]}') = '{want}'");
            Ensure(!string.IsNullOrEmpty(entry.MessageId), $"Successful['{Ids[i]}'] carries no MessageId");
        }

        var minted = succ.Keys.Order(StringComparer.Ordinal).Select(k => succ[k].MessageId).ToList();
        Ensure(minted.Distinct().Count() == goodIds.Count,
            $"the nine Successful entries minted {minted.Distinct().Count()} distinct MessageIds: [{string.Join(", ", minted)}]");

        // --- effect, and effect-absent: exactly the nine good bodies are receivable ------------
        var drained = await Drain(sqs, url, timeoutSeconds: drainS);
        Ensure(drained.ToHashSet(StringComparer.Ordinal).SetEquals(goodBodies),
            $"drained {Sorted(drained)}, expected exactly the nine good bodies " +
            $"{Sorted(goodBodies)}");
        Ensure(drained.Count == goodIds.Count,
            $"drained {drained.Count} distinct messages from a batch whose response reported " +
            $"{goodIds.Count} Successful entries: [{string.Join(", ", drained)}]");
        Ensure(!drained.Contains(""),
            "an empty-bodied message was receivable, so the entry AWS reported Failed with " +
            $"{ErrEmptyValue} enqueued anyway: [{string.Join(", ", drained)}]");
        var remaining = await Poll(
            async () => (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = url,
                AttributeNames = ["ApproximateNumberOfMessages"],
            })).Attributes!["ApproximateNumberOfMessages"],
            v => v == "0",
            timeoutSeconds: 30);
        Ensure(remaining == "0", $"queue still reports '{remaining}' visible messages after draining all nine");
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
    /// Receive-and-delete every distinct message that shows up inside timeout_s.
    ///
    /// Runs the full window on purpose: the window elapsing with nothing further arriving is
    /// the evidence that a rejected entry did NOT enqueue. Returns bodies in arrival order.
    /// </summary>
    private static async Task<List<string>> Drain(AmazonSQSClient sqs, string url, double timeoutSeconds)
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

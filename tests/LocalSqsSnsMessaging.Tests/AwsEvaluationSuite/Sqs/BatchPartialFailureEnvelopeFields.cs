// Translated from behavioral/sqs/check_batch_partial_failure_envelope_fields.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.batch-partial-failure-envelope-fields</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchPartialFailureEnvelopeFields
{
    private const string ErrEmptyValue = "EmptyValue";

    /// <summary>
    /// SendMessageBatch with an empty-body entry between two valid ones echoes the request Ids
    /// and fails only that entry with EmptyValue, SenderFault=True and "No Value Found for
    /// SendMessageBatchRequestEntry.2.MessageBody."; exactly the two valid bodies enqueue.
    /// </summary>
    [Test]
    [DisplayName("sqs.batch-partial-failure-envelope-fields")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.Batch, "an empty-body SendMessageBatch entry succeeds instead of failing with EmptyValue")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        const double drainS = 12;

        var control = await sqs.SendMessageBatchAsync(new SendMessageBatchRequest
        {
            QueueUrl = url,
            Entries =
            [
                new SendMessageBatchRequestEntry { Id = "ctl-a", MessageBody = "control-a" },
                new SendMessageBatchRequestEntry { Id = "ctl-b", MessageBody = "control-b" },
            ],
        });
        var ctlOk = (control.Successful ?? []).Select(e => e.Id).ToHashSet();
        Ensure(ctlOk.SetEquals(["ctl-a", "ctl-b"]), $"control batch Successful Ids: [{string.Join(", ", ctlOk.Order(StringComparer.Ordinal))}]");
        Ensure(control.Failed is null or { Count: 0 }, $"control batch reported failures: [{string.Join(", ", (control.Failed ?? []).Select(f => f.Id))}]");
        // A standard queue is unordered, so drained bodies are compared as a set.
        var ctlBodies = await Drain(sqs, url, timeoutS: drainS);
        Ensure(ctlBodies.ToHashSet().SetEquals(["control-a", "control-b"]), $"control drain: [{string.Join(", ", ctlBodies)}]");

        List<SendMessageBatchRequestEntry> entries =
        [
            new() { Id = "valid-1", MessageBody = "kept-body-1" },
            new() { Id = "empty-2", MessageBody = "" },
            new() { Id = "valid-3", MessageBody = "kept-body-3" },
        ];
        var resp = await sqs.SendMessageBatchAsync(new SendMessageBatchRequest { QueueUrl = url, Entries = entries });
        var succ = (resp.Successful ?? []).ToDictionary(e => e.Id);
        var fail = (resp.Failed ?? []).ToDictionary(e => e.Id);

        Ensure(succ.Keys.ToHashSet().SetEquals(["valid-1", "valid-3"]), $"Successful Ids: [{string.Join(", ", succ.Keys.Order(StringComparer.Ordinal))}]");
        Ensure(fail.Keys.ToHashSet().SetEquals(["empty-2"]), $"Failed Ids: [{string.Join(", ", fail.Keys.Order(StringComparer.Ordinal))}]");
        // Ids echo the request entry Ids exactly, not positional renumbering.
        var envelopeIds = succ.Keys.Union(fail.Keys).ToHashSet();
        Ensure(envelopeIds.SetEquals(entries.Select(e => e.Id)),
            $"envelope Ids [{string.Join(", ", envelopeIds.Order(StringComparer.Ordinal))}] do not match request Ids " +
            $"[{string.Join(", ", entries.Select(e => e.Id).Order(StringComparer.Ordinal))}]");

        var bad = fail["empty-2"];
        Ensure(bad.Code == ErrEmptyValue, $"Failed Code: '{bad.Code}'");
        Ensure(bad.SenderFault is true, $"Failed SenderFault must be True for a client error, got {bad.SenderFault}");
        const string expected = "No Value Found for SendMessageBatchRequestEntry.2.MessageBody.";
        Ensure(bad.Message == expected, $"Failed Message: expected '{expected}', got '{bad.Message}'");
        foreach (var (eid, entry) in succ)
        {
            Ensure(!string.IsNullOrEmpty(entry.MessageId), $"{eid} Successful entry has no MessageId");
            var md5 = string.IsNullOrEmpty(entry.MD5OfMessageBody) ? "" : entry.MD5OfMessageBody;
            Ensure(md5.Length == 32, $"{eid} MD5OfMessageBody is not a 32-char digest: '{md5}'");
        }

        var bodies = await Drain(sqs, url, timeoutS: drainS);
        Ensure(bodies.ToHashSet().SetEquals(["kept-body-1", "kept-body-3"]),
            $"expected exactly the two valid bodies to be enqueued, drained [{string.Join(", ", bodies)}]");
        var remaining = await Poll(
            async () => (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = url, AttributeNames = ["ApproximateNumberOfMessages"],
            })).Attributes!["ApproximateNumberOfMessages"],
            v => v == "0",
            timeoutS: 30);
        Ensure(remaining == "0", $"queue still reports {remaining} messages after draining both");
    }

    private static async Task<T> Poll<T>(Func<Task<T>> fn, Func<T, bool> pred, double timeoutS, double intervalS = 0.5)
    {
        var deadline = Stopwatch.StartNew();
        var val = await fn();
        while (!pred(val))
        {
            if (deadline.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                return val;
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalS));
            val = await fn();
        }

        return val;
    }

    /// <summary>
    /// Receive-and-delete every distinct message that shows up inside timeout_s.
    ///
    /// Runs the full window on purpose: the window elapsing with nothing further
    /// arriving is the evidence that a rejected entry did NOT enqueue.
    /// </summary>
    private static async Task<List<string>> Drain(AmazonSQSClient sqs, string url, double timeoutS)
    {
        var seen = new HashSet<string>();
        var bodies = new List<string>();
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
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

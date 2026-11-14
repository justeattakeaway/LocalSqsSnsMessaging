// Translated from behavioral/sqs/check_conc_limits_and_fanout_fifo_stale_owner_handle_rejected_after_handoff.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using System.Globalization;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.conc-limits-and-fanout-fifo-stale-owner-handle-rejected-after-handoff</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConcLimitsAndFanoutFifoStaleOwnerHandleRejectedAfterHandoff
{
    private const string Check = "conc-limits-and-fanout-fifo-stale-owner-handle-rejected-after-handoff";
    private const int N = 5;
    private const int Handoff = 3;
    private const int Vt = 300;

    /// <summary>
    /// On FIFO, after receiver B takes a message A released, A's stale handle is refused for
    /// DeleteMessage and ChangeMessageVisibility with no effect; A's current handles still work.
    /// </summary>
    [Test]
    [DisplayName("sqs.conc-limits-and-fanout-fifo-stale-owner-handle-rejected-after-handoff")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.ReceiptHandles, "a stale owner's DeleteMessage is refused with ReceiptHandleIsInvalid, not InvalidParameterValue")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqsA = ctx.Sqs;   // receiver A
        // receiver B -- a separate client, a separate consumer. (The runner caches ctx.client("sqs"),
        // so in the Python both names are the same client too; a client holds no consumer state.)
        var sqsB = ctx.Sqs;
        // FIFO only: on a standard queue a stale-handle DeleteMessage can succeed and remove the
        // message from under B, contradicting the DeleteMessage docs, so that arm is not asserted.
        var unique = FidelityContext.UniqueName("sim2real-conc-handoff-");
        var name = unique[..Math.Min(75, unique.Length)] + ".fifo";
        var url = (await sqsA.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = name,
            Attributes = new() { ["FifoQueue"] = "true", ["VisibilityTimeout"] = Vt.ToString(CultureInfo.InvariantCulture) },
            Tags = new() { ["sim2real"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqsA.DeleteQueueAsync(url));

        var resp = await sqsA.SendMessageBatchAsync(new SendMessageBatchRequest
        {
            QueueUrl = url,
            Entries = Enumerable.Range(0, N).Select(i => new SendMessageBatchRequestEntry
            {
                Id = $"e{i}", MessageBody = $"m{i}", MessageGroupId = $"g{i}", MessageDeduplicationId = $"d{i}",
            }).ToList(),
        });
        Ensure(resp.Failed is null or { Count: 0 }, $"send_message_batch failures: [{string.Join(", ", (resp.Failed ?? []).Select(f => f.Id))}]");
        var sent = resp.Successful!.Select(s => s.MessageId).ToHashSet();

        var aGot = await ReceiveExact(sqsA, url, N);
        Ensure(aGot.Keys.ToHashSet().SetEquals(sent),
            $"receiver A collected [{string.Join(", ", aGot.Values.Select(m => m.Body).Order(StringComparer.Ordinal))}], expected all {N}");
        var ordered = aGot.Keys.Order(StringComparer.Ordinal).ToList();
        var handed = ordered[..Handoff];
        var kept = ordered[Handoff..];

        // Hand three to B: A releases them, B receives exactly those three.
        foreach (var mid in handed)
        {
            await sqsA.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest { QueueUrl = url, ReceiptHandle = aGot[mid].ReceiptHandle, VisibilityTimeout = 0 });
        }

        var bGot = await ReceiveExact(sqsB, url, Handoff);
        Ensure(bGot.Keys.ToHashSet().SetEquals(handed),
            $"receiver B got [{string.Join(", ", bGot.Values.Select(m => m.Body).Order(StringComparer.Ordinal))}]; expected exactly the " +
            $"{Handoff} released messages [{string.Join(", ", handed.Select(m => aGot[m].Body).Order(StringComparer.Ordinal))}]");
        Ensure(bGot.Values.Select(m => int.Parse(m.Attributes!["ApproximateReceiveCount"], CultureInfo.InvariantCulture)).ToHashSet().SetEquals([2]),
            "assert {int(m['Attributes']['ApproximateReceiveCount']) for m in b_got.values()} == {2}");

        // The refusal: A's stale handles for B's messages.
        foreach (var mid in handed)
        {
            var stale = aGot[mid].ReceiptHandle;
            var expectedMessage = $"Value {stale} for parameter ReceiptHandle is invalid. " +
                                  "Reason: The receipt handle has expired.";
            var err = await Error(() => sqsA.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = stale }));
            Ensure(err.Code == "InvalidParameterValue",
                $"stale-owner DeleteMessage on '{aGot[mid].Body}' was refused with " +
                $"'{err.Code}', expected InvalidParameterValue ({err})");
            Ensure(err.Message == expectedMessage, $"stale-owner refusal message '{err.Message}', expected '{expectedMessage}'");
            err = await Error(() => sqsA.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest
            {
                QueueUrl = url, ReceiptHandle = stale, VisibilityTimeout = 0,
            }));
            Ensure(err.Code == "InvalidParameterValue",
                $"stale-owner ChangeMessageVisibility on '{aGot[mid].Body}' was refused with " +
                $"'{err.Code}', expected InvalidParameterValue ({err})");
            Ensure(err.Message == expectedMessage, $"stale-owner refusal message '{err.Message}', expected '{expectedMessage}'");
        }

        // A's current handles for the two it still owns work, and delete for real.
        foreach (var mid in kept)
        {
            await sqsA.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = aGot[mid].ReceiptHandle });
        }

        // B still owns all three, so a receive now returns none of them...
        var early = await ReceiveExact(sqsA, url, 1, budgetS: 6);
        Ensure(early.Count == 0,
            $"after A's refused stale-handle calls, [{string.Join(", ", early.Values.Select(m => m.Body).Order(StringComparer.Ordinal))}] " +
            "became receivable while B still owned it");
        // ...and when B releases them, exactly those three come back, received a third time.
        foreach (var (_, m) in bGot)
        {
            await sqsB.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle, VisibilityTimeout = 0 });
        }

        var again = await ReceiveExact(sqsB, url, Handoff);
        Ensure(again.Keys.ToHashSet().SetEquals(handed),
            "after B released its messages, a receive returned " +
            $"[{string.Join(", ", again.Values.Select(m => m.Body).Order(StringComparer.Ordinal))}]; expected exactly the three handed-off " +
            "messages -- a stale delete must not have removed any, and A's real deletes must hold");
        var againCounts = again.Values.Select(m => int.Parse(m.Attributes!["ApproximateReceiveCount"], CultureInfo.InvariantCulture)).ToList();
        Ensure(againCounts.ToHashSet().SetEquals([3]),
            $"receive counts [{string.Join(", ", againCounts.Order())}], expected 3 for each");
    }

    /// <summary>
    /// Return the botocore Error dict of fn(). An unexpected SUCCESS raises: an emulator
    /// accepting a call real AWS refuses must not read as an error-code mismatch.
    /// </summary>
    private static async Task<AwsError> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error ?? throw Fail($"expected a refusal, but the call SUCCEEDED and returned {result}");
    }

    private static async Task<Dictionary<string, Message>> ReceiveExact(AmazonSQSClient sqs, string url, int want, double budgetS = 30)
    {
        var got = new Dictionary<string, Message>();
        var deadline = Stopwatch.StartNew();
        while (got.Count < want && deadline.Elapsed < TimeSpan.FromSeconds(budgetS))
        {
            foreach (var m in (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2, AttributeNames = ["ApproximateReceiveCount"],
            })).Messages ?? [])
            {
                Ensure(!got.ContainsKey(m.MessageId), $"message '{m.Body}' was handed out twice to one receiver inside its window");
                got[m.MessageId] = m;
            }
        }

        return got;
    }
}

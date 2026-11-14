// Translated from behavioral/sqs/check_sqsbatch_delete_duplicate_receipt_handle_deleted_once.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using System.Globalization;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.sqsbatch-delete-duplicate-receipt-handle-deleted-once</summary>
[Category("AwsEvaluationSuite")]
public sealed class SqsbatchDeleteDuplicateReceiptHandleDeletedOnce
{
    private const string Check = "sqsbatch-delete-duplicate-receipt-handle-deleted-once";

    private const string ErrReceiptHandleInvalid = "ReceiptHandleIsInvalid";

    private const int VisibilityTimeoutSeconds = 5;

    private const string BogusHandle = "AQEBsim2real-not-a-real-receipt-handle-1111111111";

    private static Dictionary<string, string> Tags() => new() { ["sim2real:managed"] = "true", ["sim2real"] = Check };

    /// <summary>
    /// DeleteMessageBatch entries repeating one receipt handle under two Ids both succeed and delete
    /// it once; a bogus-handle entry fails alone with ReceiptHandleIsInvalid and an unnamed in-flight
    /// message redelivers untouched (entry uniqueness is by Id, not by receipt handle).
    /// </summary>
    // timing:real-time — visibility: 5 s https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/sqs-visibility-timeout.html
    [Test]
    [DisplayName("sqs.sqsbatch-delete-duplicate-receipt-handle-deleted-once")]
    [Timeout(FidelityContext.RealTimeBudgetMilliseconds)]
    [Category(ApplyDefaultCategoryAttribute.TimeBasedCategoryName)]
    [KnownFidelityGap(FidelityGap.Batch, "Successful Ids ['dup-a'], expected both duplicate entries ['dup-a', 'dup-b']: a redundant delete of an already-deleted message is idempotent, not a failure")]
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
        // Outlasts one visibility timeout with room for the arrival itself. A poll for arrival,
        // never a claim about how fast it arrives.
        var redeliverS = vis * 2;

        const string target = "dup-delete-target";
        const string bystander = "bystander-never-named";
        var sent = await sqs.SendMessageBatchAsync(new SendMessageBatchRequest
        {
            QueueUrl = url,
            Entries =
            [
                new SendMessageBatchRequestEntry { Id = "s0", MessageBody = target },
                new SendMessageBatchRequestEntry { Id = "s1", MessageBody = bystander },
            ],
        });
        Ensure(sent.Failed is null or { Count: 0 }, $"setup send batch failed: {sent.Failed?.Count}");
        var handles = await ReceiveByBody(sqs, url, want: [target, bystander], timeoutSeconds: recvS);
        Ensure(handles.Keys.ToHashSet().SetEquals([target, bystander]),
            $"receive got [{string.Join(", ", handles.Keys.Order(StringComparer.Ordinal))}] of [{target}, {bystander}] within {recvS}s; both must be " +
            "in flight before the batch delete");

        // One handle, two entry Ids, plus one bogus handle -- three entries, one call.
        var resp = await sqs.DeleteMessageBatchAsync(new DeleteMessageBatchRequest
        {
            QueueUrl = url,
            Entries =
            [
                new DeleteMessageBatchRequestEntry { Id = "dup-a", ReceiptHandle = handles[target] },
                new DeleteMessageBatchRequestEntry { Id = "dup-b", ReceiptHandle = handles[target] },
                new DeleteMessageBatchRequestEntry { Id = "bogus", ReceiptHandle = BogusHandle },
            ],
        });
        var succ = (resp.Successful ?? []).Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        var fail = (resp.Failed ?? []).ToDictionary(e => e.Id, StringComparer.Ordinal);

        Ensure(succ.SetEquals(["dup-a", "dup-b"]),
            $"Successful Ids {Sorted(succ)}, expected both duplicate entries ['dup-a', 'dup-b']: " +
            "a redundant delete of an already-deleted message is idempotent, not a failure");
        Ensure(fail.Keys.ToHashSet().SetEquals(["bogus"]), $"Failed Ids {Sorted(fail.Keys)}, expected exactly ['bogus']");
        Ensure(succ.Union(fail.Keys).ToHashSet().SetEquals(["dup-a", "dup-b", "bogus"]),
            $"envelope Ids {Sorted(succ.Union(fail.Keys))} do not partition the three request Ids");
        Ensure(fail["bogus"].Code == ErrReceiptHandleInvalid,
            $"Failed['bogus'].Code is '{fail["bogus"].Code}', expected '{ErrReceiptHandleInvalid}'");
        Ensure(fail["bogus"].Message == "The input receipt handle is invalid.",
            $"Failed['bogus'].Message is '{fail["bogus"].Message}'");
        Ensure(fail["bogus"].SenderFault == true,
            $"Failed['bogus'].SenderFault is {fail["bogus"].SenderFault}, expected True");

        // Effect and blast radius, read off the queue: the target is gone for good (one delete, not
        // two), and the bystander -- in flight, named by no entry -- comes back untouched.
        var returned = await Observe(sqs, url, timeoutSeconds: redeliverS);
        Ensure(returned.SequenceEqual([bystander]),
            $"after the visibility timeout lapsed the queue redelivered [{string.Join(", ", returned)}], expected " +
            $"exactly ['{bystander}']: '{target}' coming back would mean neither Successful entry " +
            "deleted it, and an empty result would mean the batch consumed the bystander too");

        var attrs = await Poll(
            async () => (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = url,
                AttributeNames = ["ApproximateNumberOfMessages", "ApproximateNumberOfMessagesNotVisible"],
            })).Attributes!,
            a => a["ApproximateNumberOfMessages"] == "0" && a["ApproximateNumberOfMessagesNotVisible"] == "0",
            timeoutSeconds: vis * 2);
        Ensure(attrs["ApproximateNumberOfMessages"] == "0",
            $"queue still reports '{attrs["ApproximateNumberOfMessages"]}' visible messages");
        Ensure(attrs["ApproximateNumberOfMessagesNotVisible"] == "0",
            $"queue still reports '{attrs["ApproximateNumberOfMessagesNotVisible"]}' in flight");
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
    /// Deletes nothing -- the caller owns the handles.
    /// </summary>
    private static async Task<Dictionary<string, string>> ReceiveByBody(AmazonSQSClient sqs, string url, string[] want, double timeoutSeconds)
    {
        var handles = new Dictionary<string, string>(StringComparer.Ordinal);
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutSeconds) && handles.Count < want.Length)
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

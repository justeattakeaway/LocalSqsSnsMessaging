// Translated from behavioral/sqs/check_batch_total_size_exceeds_limit_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using System.Globalization;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.batch-total-size-exceeds-limit-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchTotalSizeExceedsLimitRejected
{
    private const string Check = "batch-total-size-exceeds-limit-rejected";

    // The aggregate limit for one SendMessageBatch is the queue's MaximumMessageSize (a new queue
    // reports 1048576), so it is read off the queue; this is only the documented floor.
    private const int MinPlausibleLimit = 262144;
    // awsQueryCompatible code for BatchRequestTooLong, which botocore surfaces as Error.Code.
    private const string ErrBatchTooLong = "AWS.SimpleQueueService.BatchRequestTooLong";

    /// <summary>
    /// A SendMessageBatch of individually legal entries whose total exceeds MaximumMessageSize is
    /// refused with BatchRequestTooLong and enqueues nothing; the same entries split in two succeed.
    /// </summary>
    [Test]
    [DisplayName("sqs.batch-total-size-exceeds-limit-rejected")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.DefaultAttributes, "GetQueueAttributes(MaximumMessageSize) on a fresh queue returns no Attributes (KeyError 'Attributes')")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-"),
            Tags = new() { ["sim2real:managed"] = "true", ["sim2real"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        const double drainS = 15;
        // Negative window: poll for a violation to APPEAR. Kept short because the drain below
        // is the stronger absence evidence and this window is spent whether or not it fires.
        const double attrS = 8;

        var limit = int.Parse((await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = url, AttributeNames = ["MaximumMessageSize"],
        })).Attributes!["MaximumMessageSize"], CultureInfo.InvariantCulture);
        Ensure(limit >= MinPlausibleLimit,
            $"MaximumMessageSize={limit} is below the smallest bound SQS documents " +
            $"({MinPlausibleLimit}); the queue attribute cannot be trusted as the batch bound");

        // Four entries at limit//3 + 1: each is individually legal (a third of the limit), their sum
        // (~4/3 of the limit) is not.
        var entryBytes = limit / 3 + 1;
        Ensure(entryBytes < limit, "test entry must be individually legal");
        var over = Enumerable.Range(0, 4)
            .Select(i => new SendMessageBatchRequestEntry { Id = $"big-{i}", MessageBody = new string((char)('a' + i), entryBytes) })
            .ToList();
        var totalBytes = over.Sum(e => e.MessageBody.Length);
        Ensure(totalBytes > limit, "batch must exceed the limit");

        var (code, message) = await Err(() => sqs.SendMessageBatchAsync(new SendMessageBatchRequest { QueueUrl = url, Entries = over }));
        Ensure(code == ErrBatchTooLong,
            $"expected {ErrBatchTooLong} for a {totalBytes}-byte batch of 4 individually-legal " +
            $"{entryBytes}-byte entries, got '{code}' ('{message}')");
        // The refusal names the bound it enforced and the size it measured, so a BatchRequestTooLong
        // raised for some other reason (or against a different, hardcoded limit) cannot satisfy it.
        var expectedMessage = $"Batch requests cannot be longer than {limit} bytes. You have sent {totalBytes} bytes.";
        Ensure(message == expectedMessage, $"expected refusal message '{expectedMessage}', got '{message}'");

        // All-or-nothing: none of the four entries enqueued.
        var depth = await Poll(
            async () => (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = url, AttributeNames = ["ApproximateNumberOfMessages"],
            })).Attributes!["ApproximateNumberOfMessages"],
            v => v != "0",
            timeoutS: attrS);
        Ensure(depth == "0", $"rejected batch enqueued something: ApproximateNumberOfMessages={depth}");
        var leaked = await Drain(sqs, url, timeoutS: drainS);
        Ensure(leaked.Count == 0, $"rejected batch delivered {leaked.Count} message(s) of sizes [{string.Join(", ", leaked)}]");

        // The same entries, split so each request is under the limit, succeed.
        foreach (var half in new[] { over[..2], over[2..] })
        {
            var resp = await sqs.SendMessageBatchAsync(new SendMessageBatchRequest { QueueUrl = url, Entries = half });
            Ensure(resp.Failed is null or { Count: 0 }, $"split batch failed: [{string.Join(", ", (resp.Failed ?? []).Select(f => f.Id))}]");
            Ensure(resp.Successful!.Select(e => e.Id).ToHashSet().SetEquals(half.Select(e => e.Id)),
                $"split batch Successful Ids [{string.Join(", ", resp.Successful!.Select(e => e.Id).Order(StringComparer.Ordinal))}]");
        }

        var sizes = await Drain(sqs, url, timeoutS: drainS);
        Ensure(sizes.Order().SequenceEqual(Enumerable.Repeat(entryBytes, 4)),
            $"expected 4 messages of {entryBytes} bytes after the split sends, got [{string.Join(", ", sizes.Order())}]");
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

    /// <summary>Return (Error.Code, Error.Message) for the ClientError fn raises; raise if fn succeeds.</summary>
    private static async Task<(string? Code, string Message)> Err<T>(Func<Task<T>> fn)
        where T : class
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is not null)
        {
            return (error.Code, error.Message ?? "");
        }

        throw Fail("expected the call to be refused with an error, but it succeeded");
    }

    /// <summary>Receive-and-delete every distinct message inside timeout_s; returns their sizes.</summary>
    private static async Task<List<int>> Drain(AmazonSQSClient sqs, string url, double timeoutS)
    {
        var seen = new HashSet<string>();
        var sizes = new List<int>();
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 1 });
            foreach (var msg in resp.Messages ?? [])
            {
                if (seen.Add(msg.MessageId))
                {
                    sizes.Add(msg.Body.Length);
                }

                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle });
            }
        }

        return sizes;
    }
}

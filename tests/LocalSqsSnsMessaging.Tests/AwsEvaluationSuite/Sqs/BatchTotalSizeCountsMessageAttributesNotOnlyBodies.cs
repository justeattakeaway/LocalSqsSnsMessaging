// Translated from behavioral/sqs/check_batch_total_size_counts_message_attributes_not_only_bodies.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.batch-total-size-counts-message-attributes-not-only-bodies</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchTotalSizeCountsMessageAttributesNotOnlyBodies
{
    private const string Check = "batch-total-size-counts-message-attributes-not-only-bodies";

    // A fixed API constant (the documented floor of MaximumMessageSize's own allowed range), used
    // only to sanity-check what the queue reports -- never assumed to BE the batch bound.
    private const int MinPlausibleLimit = 262144;
    private const string ErrBatchTooLong = "AWS.SimpleQueueService.BatchRequestTooLong";

    /// <summary>
    /// SendMessageBatch's aggregate size bound counts MessageAttributes, not only bodies: two
    /// tiny-body entries with large attributes are refused with BatchRequestTooLong; none enqueue.
    /// </summary>
    [Test]
    [DisplayName("sqs.batch-total-size-counts-message-attributes-not-only-bodies")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-battrsize-"),
            Tags = new() { ["sim2real:managed"] = "true", ["sim2real"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var limit = int.Parse((await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = url, AttributeNames = ["MaximumMessageSize"],
        })).Attributes!["MaximumMessageSize"], CultureInfo.InvariantCulture);
        Ensure(limit >= MinPlausibleLimit,
            $"MaximumMessageSize={limit} is below the smallest bound SQS documents " +
            $"({MinPlausibleLimit}); the queue attribute cannot be trusted as the batch bound");

        // Each entry: a 5-byte body plus one String attribute named "blob" (4 bytes) whose value is
        // sized so one entry alone -- body + name + "String" (6) + value -- stays under the limit
        // (individually legal), but two entries together exceed it.
        var attrValueLen = limit / 2 + 100;
        const string body = "small";
        const string attrName = "blob";

        SendMessageBatchRequestEntry Entry(string entryId) => new()
        {
            Id = entryId,
            MessageBody = body,
            MessageAttributes = new()
            {
                [attrName] = new MessageAttributeValue { DataType = "String", StringValue = new string('x', attrValueLen) },
            },
        };

        var perEntryBytes = body.Length + attrName.Length + "String".Length + attrValueLen;
        Ensure(perEntryBytes < limit, "test entry must be individually legal");
        List<SendMessageBatchRequestEntry> entries = [Entry("e0"), Entry("e1")];
        var totalBytes = perEntryBytes * 2;
        Ensure(totalBytes > limit, "batch must exceed the limit once both entries are summed");
        // Total BODY bytes alone -- what a body-only accounting would see -- stays tiny, so a
        // refusal here can only be explained by attributes counting toward the aggregate too.
        var totalBodyBytes = entries.Sum(e => e.MessageBody.Length);
        Ensure(totalBodyBytes < 100, $"test body total {totalBodyBytes} is not tiny as intended");

        var (code, message) = await Err(() => sqs.SendMessageBatchAsync(new SendMessageBatchRequest { QueueUrl = url, Entries = entries }));
        Ensure(code == ErrBatchTooLong,
            $"expected {ErrBatchTooLong} for a {totalBytes}-byte (attribute-driven) batch of " +
            $"2 individually-legal {perEntryBytes}-byte entries with only {totalBodyBytes} " +
            $"total body bytes, got '{code}' ('{message}')");
        var expectedMessage = $"Batch requests cannot be longer than {limit} bytes. You have sent {totalBytes} bytes.";
        Ensure(message == expectedMessage, $"expected refusal message '{expectedMessage}', got '{message}'");

        // All-or-nothing: neither entry enqueued.
        var depth = await Poll(
            async () => (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = url, AttributeNames = ["ApproximateNumberOfMessages"],
            })).Attributes!["ApproximateNumberOfMessages"],
            v => v != "0",
            timeoutS: 8);
        Ensure(depth == "0", $"rejected batch enqueued something: ApproximateNumberOfMessages={depth}");
        var leaked = await Drain(sqs, url, timeoutS: 10);
        Ensure(leaked.Count == 0, $"rejected batch delivered {leaked.Count} message(s): [{string.Join(", ", leaked)}]");

        // The same entries sent one at a time succeed, so the refusal was about the aggregate.
        foreach (var e in entries)
        {
            var resp = await sqs.SendMessageBatchAsync(new SendMessageBatchRequest { QueueUrl = url, Entries = [e] });
            Ensure(resp.Failed is null or { Count: 0 }, $"split send of '{e.Id}' failed: [{string.Join(", ", (resp.Failed ?? []).Select(f => f.Id))}]");
            Ensure(resp.Successful!.Select(x => x.Id).SequenceEqual([e.Id]),
                $"split send of '{e.Id}' answered [{string.Join(", ", resp.Successful!.Select(x => x.Id))}]");
        }

        var delivered = await Drain(sqs, url, timeoutS: 20);
        Ensure(delivered.Order(StringComparer.Ordinal).SequenceEqual([body, body]),
            $"split sends delivered [{string.Join(", ", delivered)}], expected two '{body}' messages");
    }

    /// <summary>Return (Error.Code, Error.Message) for the ClientError fn raises; raise if fn succeeds.</summary>
    private static async Task<(string? Code, string Message)> Err<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message ?? "");
        }

        throw Fail($"expected the batch to be refused, but it succeeded: {result}");
    }

    private static async Task<T> Poll<T>(Func<Task<T>> fn, Func<T, bool> pred, double timeoutS, double intervalS = 0.5)
    {
        var deadline = FidelityTime.StartNew();
        var val = await fn();
        while (!pred(val))
        {
            if (deadline.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                return val;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
            val = await fn();
        }

        return val;
    }

    private static async Task<List<string>> Drain(AmazonSQSClient sqs, string url, double timeoutS)
    {
        var seen = new HashSet<string>();
        var bodies = new List<string>();
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            foreach (var msg in (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 1 })).Messages ?? [])
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

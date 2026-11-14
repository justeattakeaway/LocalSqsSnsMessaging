// Translated from behavioral/sqs/check_dlq_move_preserves_standard_enqueue_timestamp_and_resets_it_for_fifo.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.dlq-move-preserves-standard-enqueue-timestamp-and-resets-it-for-fifo</summary>
[Category("AwsEvaluationSuite")]
public sealed class DlqMovePreservesStandardEnqueueTimestampAndResetsItForFifo
{
    private const double MoveSeconds = 90.0; // bounded wait per arm for the asynchronous source -> DLQ move
    private const int MaxReceiveCount = 1;
    private const string DlqRetentionSeconds = "60"; // the SQS API MINIMUM for MessageRetentionPeriod; cannot be made shorter
    // Hold the message untouched on the source so the DLQ's 60s retention boundary is nearly reached
    // on arrival if the enqueue instant carries over. Only the DLQ has the short retention because the
    // retention expiry can fire measurably earlier than its nominal boundary.
    private const double HoldSeconds = 40.0;
    // Long enough to see the standard copy expire, short enough to stay inside the FIFO copy's reset
    // 60s boundary.
    private const double BoundaryCheckSeconds = 45.0;

    private static readonly Dictionary<string, string> Tags = new()
    {
        ["sim2real:managed"] = "true",
        ["sim2real"] = "dlq-enqueue-timestamp",
    };

    // timing:real-time — retention: 60 s https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/sqs-dead-letter-queues.html
    /// <summary>
    /// A DLQ move keeps a standard message's enqueue instant and resets a FIFO message's: the
    /// standard copy expires under the DLQ's 60s retention while the FIFO copy survives.
    ///
    /// Reference: https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/sqs-dead-letter-queues.html
    /// </summary>
    [Test]
    [DisplayName("sqs.dlq-move-preserves-standard-enqueue-timestamp-and-resets-it-for-fifo")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [Category(ApplyDefaultCategoryAttribute.TimeBasedCategoryName)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.RealTimeBudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;

        var (stdSrc, stdDlq) = await MakePair(ctx, sqs, fifo: false);
        var (fifoSrc, fifoDlq) = await MakePair(ctx, sqs, fifo: true);

        var token = Guid.NewGuid().ToString("N")[..8];
        var stdBody = $"std-poison-{token}";
        var fifoBody = $"fifo-poison-{token}";
        var groupId = $"grp-{token}";

        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = stdSrc, MessageBody = stdBody });
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = fifoSrc, MessageBody = fifoBody, MessageGroupId = groupId });

        var stdFirst = await AwaitDelivery(sqs, stdSrc, token: token, timeoutSeconds: 30.0, what: "standard source, 1st delivery");
        var fifoFirst = await AwaitDelivery(sqs, fifoSrc, token: token, timeoutSeconds: 30.0, what: "FIFO source, 1st delivery");
        var stdSrcMs = SentMs(stdFirst, where: "standard source");
        var fifoSrcMs = SentMs(fifoFirst, where: "FIFO source");
        Ensure(stdFirst.Attributes!["ApproximateReceiveCount"] == "1",
            "the standard source's first delivery reported ApproximateReceiveCount=" +
            $"{stdFirst.Attributes["ApproximateReceiveCount"]}, expected '1'; the message had " +
            "already been delivered before this check looked at it");
        Ensure(fifoFirst.Attributes!["ApproximateReceiveCount"] == "1",
            "the FIFO source's first delivery reported ApproximateReceiveCount=" +
            $"{fifoFirst.Attributes["ApproximateReceiveCount"]}, expected '1'");

        await HoldWithoutReceiving(HoldSeconds);

        // Force the 2nd delivery attempt on both, which exceeds maxReceiveCount=1 and triggers the move.
        await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest { QueueUrl = stdSrc, ReceiptHandle = stdFirst.ReceiptHandle, VisibilityTimeout = 0 });
        await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest { QueueUrl = fifoSrc, ReceiptHandle = fifoFirst.ReceiptHandle, VisibilityTimeout = 0 });

        var stdMoved = await AwaitDlqMove(
            sqs,
            stdSrc,
            stdDlq,
            token: token,
            timeoutSeconds: MoveSeconds,
            what: "standard DLQ (maxReceiveCount=1 exceeded, so the move is due)");
        var fifoMoved = await AwaitDlqMove(
            sqs,
            fifoSrc,
            fifoDlq,
            token: token,
            timeoutSeconds: MoveSeconds,
            what: "FIFO DLQ (maxReceiveCount=1 exceeded, so the move is due)");
        var stdDlqMs = SentMs(stdMoved, where: "standard DLQ");
        var fifoDlqMs = SentMs(fifoMoved, where: "FIFO DLQ");

        Ensure(stdMoved.Body == stdBody,
            $"the standard DLQ copy carried {stdMoved.Body}, expected {stdBody}");
        Ensure(fifoMoved.Body == fifoBody,
            $"the FIFO DLQ copy carried {fifoMoved.Body}, expected {fifoBody}");

        // _await_dlq_move's receive hid each copy for the DLQ's default 30s VisibilityTimeout; release
        // them so the boundary poll does not read that as an early expiry.
        await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest { QueueUrl = stdDlq, ReceiptHandle = stdMoved.ReceiptHandle, VisibilityTimeout = 0 });
        await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest { QueueUrl = fifoDlq, ReceiptHandle = fifoMoved.ReceiptHandle, VisibilityTimeout = 0 });

        // SentTimestamp is the immutable original-send instant on both queue types.
        Ensure(stdDlqMs == stdSrcMs,
            "a STANDARD queue's SentTimestamp must be unchanged by a DLQ move: the source copy " +
            $"reported {stdSrcMs} and the DLQ copy {stdDlqMs} " +
            $"(a {stdDlqMs - stdSrcMs}ms shift)");
        Ensure(fifoDlqMs == fifoSrcMs,
            "a FIFO queue's SentTimestamp is the message's immutable original-send instant and " +
            $"must also be unchanged by a DLQ move: the source copy reported {fifoSrcMs} and " +
            $"the DLQ copy {fifoDlqMs} (a {fifoDlqMs - fifoSrcMs}ms shift)");

        // The documented enqueue-timestamp rule is observable only through retention expiry.
        await AwaitRetentionBoundarySplit(
            sqs,
            stdDlq,
            fifoDlq,
            stdToken: token,
            fifoToken: token,
            timeoutSeconds: BoundaryCheckSeconds);
    }

    private static async Task<string> QueueArn(AmazonSQSClient sqs, string url)
        => (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];

    /// <summary>
    /// Create a (source, dlq) pair of the same type, wired by a maxReceiveCount=1 RedrivePolicy.
    ///
    /// A FIFO queue's DLQ must itself be FIFO and vice versa.
    /// </summary>
    private static async Task<(string Src, string Dlq)> MakePair(FidelityContext ctx, AmazonSQSClient sqs, bool fifo)
    {
        var suffix = fifo ? ".fifo" : "";
        var attrs = fifo
            ? new Dictionary<string, string> { ["FifoQueue"] = "true", ["ContentBasedDeduplication"] = "true" }
            : [];

        var dlqAttrs = new Dictionary<string, string>(attrs)
        {
            // Only the DLQ carries the 60s minimum -- see _HOLD_S for why the source does not.
            ["MessageRetentionPeriod"] = DlqRetentionSeconds,
        };
        var dlqUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-dlq-") + suffix,
            Attributes = dlqAttrs,
            Tags = new(Tags),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(dlqUrl));

        var srcAttrs = new Dictionary<string, string>(attrs)
        {
            // Must exceed _HOLD_S: ChangeMessageVisibility after the message has returned to visible on its
            // own fails with ReceiptHandleIsInvalid ("the receipt handle has expired").
            ["VisibilityTimeout"] = ((int)HoldSeconds + 60).ToString(),
            ["RedrivePolicy"] = PyDumps(("deadLetterTargetArn", await QueueArn(sqs, dlqUrl)), ("maxReceiveCount", MaxReceiveCount)),
        };
        var srcUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-src-") + suffix,
            Attributes = srcAttrs,
            Tags = new(Tags),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(srcUrl));
        return (srcUrl, dlqUrl);
    }

    /// <summary>
    /// Bounded poll until the tokened message is delivered from `url`; RAISES on the deadline.
    ///
    /// Non-matching messages are re-exposed with ChangeMessageVisibility(0) -- on a FIFO queue that
    /// also matters for progress, because an in-flight message blocks its whole message group.
    /// </summary>
    private static async Task<Message> AwaitDelivery(AmazonSQSClient sqs, string url, string token, double timeoutSeconds, string what)
    {
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed.TotalSeconds < timeoutSeconds)
        {
            foreach (var msg in (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
                     {
                         QueueUrl = url,
                         MaxNumberOfMessages = 10,
                         WaitTimeSeconds = 2,
                         MessageSystemAttributeNames = ["All"],
                     })).Messages ?? [])
            {
                if (msg.Body.Contains(token, StringComparison.Ordinal))
                {
                    return msg;
                }

                await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle, VisibilityTimeout = 0 });
            }
        }

        throw Fail($"{what}: {token} was not delivered from {url} within {timeoutSeconds}s");
    }

    /// <summary>
    /// Drive and observe the source -> DLQ move; RAISES on the deadline.
    ///
    /// SQS evaluates the redrive policy when a ReceiveMessage ATTEMPTS to hand the message back, so
    /// each iteration pokes the SOURCE (re-exposing whatever it returns) and then reads the DLQ.
    /// Polling the DLQ alone can wait forever on a source nobody is consuming.
    /// </summary>
    private static async Task<Message> AwaitDlqMove(AmazonSQSClient sqs, string srcUrl, string dlqUrl, string token, double timeoutSeconds, string what)
    {
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed.TotalSeconds < timeoutSeconds)
        {
            foreach (var msg in (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = srcUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 1 })).Messages ?? [])
            {
                await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest { QueueUrl = srcUrl, ReceiptHandle = msg.ReceiptHandle, VisibilityTimeout = 0 });
            }

            foreach (var msg in (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
                     {
                         QueueUrl = dlqUrl,
                         MaxNumberOfMessages = 10,
                         WaitTimeSeconds = 1,
                         MessageSystemAttributeNames = ["All"],
                     })).Messages ?? [])
            {
                if (msg.Body.Contains(token, StringComparison.Ordinal))
                {
                    return msg;
                }

                await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest { QueueUrl = dlqUrl, ReceiptHandle = msg.ReceiptHandle, VisibilityTimeout = 0 });
            }
        }

        throw Fail($"{what}: {token} did not reach the DLQ within {timeoutSeconds}s");
    }

    private static long SentMs(Message msg, string where)
    {
        var raw = msg.Attributes!["SentTimestamp"];
        Ensure(raw.All(char.IsAsciiDigit) && raw.Length == 13,
            $"SentTimestamp on the {where} copy must be 13-digit epoch milliseconds, got {raw}");
        return long.Parse(raw);
    }

    /// <summary>
    /// A deliberate wait with no SQS interaction: elapsed time is the experimental variable.
    ///
    /// Any receive here would count as a delivery against maxReceiveCount=1 and move the message early.
    /// </summary>
    private static async Task HoldWithoutReceiving(double seconds)
    {
        var start = FidelityTime.StartNew();
        while (start.Elapsed.TotalSeconds < seconds)
        {
            await FidelityTime.Delay(TimeSpan.FromSeconds(Math.Max(0, Math.Min(1.0, seconds - start.Elapsed.TotalSeconds))));
        }
    }

    /// <summary>
    /// Poll both DLQs together for `timeout_s`: the standard copy must expire (its retention clock
    /// carried over) while the FIFO copy stays receivable throughout (its clock reset).
    /// </summary>
    private static async Task AwaitRetentionBoundarySplit(AmazonSQSClient sqs, string stdDlq, string fifoDlq, string stdToken, string fifoToken, double timeoutSeconds)
    {
        // VisibilityTimeout=0 on the receive keeps messages visible; a follow-up ChangeMessageVisibility
        // could race the standard copy's own retention expiry.
        var deadline = FidelityTime.StartNew();
        var stdFound = true;
        while (deadline.Elapsed.TotalSeconds < timeoutSeconds)
        {
            stdFound = false;
            foreach (var msg in (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
                     {
                         QueueUrl = stdDlq,
                         MaxNumberOfMessages = 10,
                         WaitTimeSeconds = 1,
                         VisibilityTimeout = 0,
                     })).Messages ?? [])
            {
                if (msg.Body.Contains(stdToken, StringComparison.Ordinal))
                {
                    stdFound = true;
                }
            }

            var fifoFound = false;
            foreach (var msg in (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
                     {
                         QueueUrl = fifoDlq,
                         MaxNumberOfMessages = 10,
                         WaitTimeSeconds = 1,
                         VisibilityTimeout = 0,
                     })).Messages ?? [])
            {
                if (msg.Body.Contains(fifoToken, StringComparison.Ordinal))
                {
                    fifoFound = true;
                }
            }

            Ensure(fifoFound,
                $"the FIFO DLQ copy {fifoToken} disappeared from {fifoDlq} before the {timeoutSeconds}s " +
                "check window elapsed -- it expired as though its enqueue timestamp had NOT reset");
        }

        Ensure(!stdFound,
            $"the standard DLQ copy {stdToken} was still receivable from {stdDlq} after " +
            $"{timeoutSeconds}s -- its enqueue timestamp did not carry over from the original send");
    }

    /// <summary>Python's <c>json.dumps</c> of a flat dict, with its default separators.</summary>
    private static string PyDumps(params (string Key, object Value)[] items)
        => "{" + string.Join(", ", items.Select(kv => $"{JsonSerializer.Serialize(kv.Key)}: {JsonSerializer.Serialize(kv.Value)}")) + "}";
}

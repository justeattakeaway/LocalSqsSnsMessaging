// Translated from behavioral/sqs/check_max_size_message_body_round_trip.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.max-size-message-body-round-trip</summary>
[Category("AwsEvaluationSuite")]
public sealed class MaxSizeMessageBodyRoundTrip
{
    private const int MaxBodyBytes = 262144;  // 256 KiB, below the 1 MiB default MaximumMessageSize

    /// <summary>
    /// A 262144-byte (256 KiB) message body is accepted, stored and returned byte-for-byte
    /// unchanged (no silent truncation of a large body).
    /// </summary>
    [Test]
    [DisplayName("sqs.max-size-message-body-round-trip")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-maxsz-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        // Single-byte ASCII repeated so character count == byte count.
        var body = new string('x', MaxBodyBytes - 1) + "y";
        Ensure(Encoding.UTF8.GetByteCount(body) == MaxBodyBytes,
            $"test body is {Encoding.UTF8.GetByteCount(body)} bytes, expected exactly {MaxBodyBytes}");

        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = body });

        var received = await PollForMessage(ctx, url);
        Ensure(received.Body.Length == MaxBodyBytes,
            $"received body is {received.Body.Length} bytes, expected {MaxBodyBytes} " +
            "(possible silent truncation of a large body)");
        Ensure(received.Body == body, "received body content diverges from the sent body");
        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = received.ReceiptHandle });

        // Independent read: the deleted message must not still be deliverable.
        var leftover = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 2 })).Messages ?? [];
        Ensure(leftover.Count == 0, $"deleted message is still visible on the queue: {leftover.Count} message(s)");
    }

    private static async Task<Message> PollForMessage(FidelityContext ctx, string url, double timeoutS = 30.0)
    {
        var sqs = ctx.Sqs;
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed.TotalSeconds < timeoutS)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 5 });
            var msgs = resp.Messages ?? [];
            if (msgs.Count > 0)
            {
                return msgs[0];
            }
        }

        throw Fail("no message received within timeout");
    }
}

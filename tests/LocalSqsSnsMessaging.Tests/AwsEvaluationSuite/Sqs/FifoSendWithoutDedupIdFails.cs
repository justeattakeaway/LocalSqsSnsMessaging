// Translated from behavioral/sqs/check_fifo_send_without_dedup_id_fails.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.fifo-send-without-dedup-id-fails</summary>
[Category("AwsEvaluationSuite")]
public sealed class FifoSendWithoutDedupIdFails
{
    private const string NoDedupIdMessage =
        "The queue should either have ContentBasedDeduplication enabled or MessageDeduplicationId " +
        "provided explicitly";

    /// <summary>
    /// FIFO send without dedup id and without ContentBasedDeduplication MUST be rejected with the
    /// specific error, must leave no message on the queue, and the same queue must still accept a
    /// correctly-formed send (dedup id supplied) end-to-end.
    /// </summary>
    [Test]
    [DisplayName("sqs.fifo-send-without-dedup-id-fails")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var (_, url) = await CreateQueue(ctx, fifo: true);

        var error = await ClientError(() => sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "x", MessageGroupId = "g1" }));
        Ensure(error.Code == "InvalidParameterValue",
            "FIFO send lacking MessageDeduplicationId on a queue without ContentBasedDeduplication " +
            $"was rejected with '{error.Code}', expected 'InvalidParameterValue'");
        Ensure(error.Message == NoDedupIdMessage,
            $"expected message '{NoDedupIdMessage}', got '{error.Message}'");

        var attrs = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = url, AttributeNames = ["ApproximateNumberOfMessages"],
        })).Attributes!;
        Ensure(attrs["ApproximateNumberOfMessages"] == "0",
            $"queue depth was '{attrs["ApproximateNumberOfMessages"]}' after a rejected send, " +
            "expected '0'");
        var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 });
        Ensure(resp.Messages is null or { Count: 0 },
            "message(s) landed on the queue despite the rejected SendMessage call: " +
            $"[{string.Join(", ", (resp.Messages ?? []).Select(m => m.Body))}]");

        // Positive control: the identical queue, same call shape, with MessageDeduplicationId
        // supplied, succeeds end-to-end -- this is what separates "rejects a send lacking a dedup
        // id" from "rejects every send to this queue".
        var sendResp = await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = url, MessageBody = "ok", MessageGroupId = "g1", MessageDeduplicationId = "d1",
        });
        Ensure(sendResp.MessageId is not null, $"corrected send did not return a MessageId: {sendResp}");
        var msg = await PollReceiveOne(sqs, url, timeoutS: 15.0);
        Ensure(msg.Body == "ok", $"received body '{msg.Body}', expected 'ok'");
        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle });
    }

    /// <summary>
    /// Call fn(); return its botocore Error dict. An unexpected SUCCESS raises, because an emulator
    /// ACCEPTING input real AWS refuses is the strongest signal available.
    /// </summary>
    private static async Task<AwsError> ClientError<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error ?? throw Fail($"expected the send to be rejected, but it SUCCEEDED: {Repr(result)}");
    }

    private static string Repr(object? result) => result is SendMessageResponse r
        ? $"{{'MD5OfMessageBody': '{r.MD5OfMessageBody}', 'MessageId': '{r.MessageId}'}}"
        : $"{result}";

    /// <summary>Create a queue, register its teardown, and return (name, queue_url).</summary>
    private static async Task<(string Name, string Url)> CreateQueue(FidelityContext ctx, bool fifo = false, Dictionary<string, string>? attributes = null)
    {
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-sqs-check-");
        if (fifo)
        {
            name = $"{name}.fifo";
        }

        var attrs = new Dictionary<string, string>(attributes ?? []);
        if (fifo)
        {
            attrs.TryAdd("FifoQueue", "true");
        }

        var request = new CreateQueueRequest { QueueName = name, Tags = new() { ["sim2real"] = "behavioral-check" } };
        if (attrs.Count > 0)
        {
            request.Attributes = attrs;
        }

        var url = (await sqs.CreateQueueAsync(request)).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        return (name, url);
    }

    private static async Task<Message> PollReceiveOne(AmazonSQSClient sqs, string url, double timeoutS = 15.0)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 3 });
            if (resp.Messages is { Count: > 0 } messages)
            {
                return messages[0];
            }
        }

        throw Fail($"no message received within {timeoutS}s");
    }
}

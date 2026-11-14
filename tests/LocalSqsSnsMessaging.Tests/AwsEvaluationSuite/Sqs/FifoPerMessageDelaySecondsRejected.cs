// Translated from behavioral/sqs/check_fifo_per_message_delay_seconds_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.Runtime;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.fifo-per-message-delay-seconds-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class FifoPerMessageDelaySecondsRejected
{
    private const int QueueDelayS = 10;
    private const string DelayMessage =
        "Value 5 for parameter DelaySeconds is invalid. Reason: The request include parameter that " +
        "is not valid for this queue type.";

    /// <summary>
    /// A FIFO queue refuses a per-message DelaySeconds in SendMessage and refuses a whole
    /// SendMessageBatch with one such entry; DelaySeconds=0 is accepted, and accepted messages are
    /// held back by the queue-level delay and then delivered in order.
    /// </summary>
    [Test]
    [DisplayName("sqs.fifo-per-message-delay-seconds-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-fifodelay-") + ".fifo",
            Attributes = new()
            {
                ["FifoQueue"] = "true",
                ["ContentBasedDeduplication"] = "true",
                ["DelaySeconds"] = QueueDelayS.ToString(CultureInfo.InvariantCulture),
            },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var err = await Error(() => sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = url, MessageBody = "per-message-delay", MessageGroupId = "g", DelaySeconds = 5,
        }));
        Ensure(err.Code == "InvalidParameterValue",
            $"SendMessage DelaySeconds=5 on a FIFO queue gave code '{err.Code}'");
        Ensure(err.Message == DelayMessage,
            $"expected message '{DelayMessage}', got '{err.Message}'");

        err = await Error(() => sqs.SendMessageBatchAsync(new SendMessageBatchRequest
        {
            QueueUrl = url,
            Entries =
            [
                new SendMessageBatchRequestEntry { Id = "plain", MessageBody = "batch-sibling", MessageGroupId = "g" },
                new SendMessageBatchRequestEntry
                {
                    Id = "delayed", MessageBody = "batch-delayed", MessageGroupId = "g",
                    DelaySeconds = 5,
                },
            ],
        }));
        Ensure(err.Code == "InvalidParameterValue",
            "SendMessageBatch with one DelaySeconds=5 entry on a FIFO queue gave code " +
            $"'{err.Code}'; a FIFO batch is refused as a whole request");
        Ensure(err.Message == DelayMessage,
            $"expected message '{DelayMessage}', got '{err.Message}'");

        // positive controls: no per-message delay, and an explicit DelaySeconds=0
        var sentAt = FidelityTime.StartNew();
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "plain", MessageGroupId = "g" });
        try
        {
            await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "zero-delay", MessageGroupId = "g", DelaySeconds = 0 });
        }
        catch (AmazonServiceException exc)
        {
            throw new FidelityAssertionException(
                $"DelaySeconds=0 on a FIFO queue must be accepted, got ({exc.ErrorCode}, {exc.Message})", exc);
        }

        var early = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 0 })).Messages ?? [];
        if (sentAt.Elapsed.TotalSeconds < QueueDelayS)
        {
            Ensure(early.Count == 0,
                $"messages were receivable before the queue's DelaySeconds={QueueDelayS} elapsed: " +
                $"[{string.Join(", ", early.Select(m => m.Body))}]");
        }

        List<string> bodies = [.. early.Select(m => m.Body)];
        foreach (var m in early)
        {
            await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle });
        }

        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(QueueDelayS + 30) && bodies.Count < 2)
        {
            foreach (var m in (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 })).Messages ?? [])
            {
                bodies.Add(m.Body);
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle });
            }
        }

        Ensure(bodies.SequenceEqual(["plain", "zero-delay"]),
            "after the queue delay the FIFO group must deliver exactly the two accepted messages in " +
            $"send order, and none of the refused ones; received [{string.Join(", ", bodies)}]");
        var trailing = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 })).Messages ?? [];
        Ensure(trailing.Count == 0,
            $"a refused send was enqueued after all: [{string.Join(", ", trailing.Select(m => m.Body))}]");
    }

    /// <summary>
    /// The botocore Error dict ``fn`` raises. An unexpected success raises too, naming what came
    /// back: a FIFO queue that accepts a per-message delay must not read as a code mismatch.
    /// </summary>
    private static async Task<AwsError> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error ?? throw Fail($"expected the call to be refused, but it succeeded: {result}");
    }
}

// Translated from behavioral/sqs/check_fifo_dedup_survives_consumer_delete.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using System.Numerics;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.fifo-dedup-survives-consumer-delete</summary>
[Category("AwsEvaluationSuite")]
public sealed class FifoDedupSurvivesConsumerDelete
{
    /// <summary>
    /// A FIFO dedup id stays claimed after the consumer deletes the message: a retry with the same
    /// id returns the original MessageId and SequenceNumber and is never delivered, while a new id is.
    /// </summary>
    [Test]
    [DisplayName("sqs.fifo-dedup-survives-consumer-delete")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-dedupdel-") + ".fifo",
            Attributes = new() { ["FifoQueue"] = "true" },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var first = await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = url,
            MessageBody = "charge-order-42",
            MessageGroupId = "orders",
            MessageDeduplicationId = "order-42",
        });
        var got = await ReceiveOne(sqs, url, timeoutSeconds: 30);
        Ensure(got is not null && got.Body == "charge-order-42",
            $"the first send was not delivered; received {(got is null ? "None" : got.Body)}");
        Ensure(got!.Attributes!.GetValueOrDefault("MessageDeduplicationId") == "order-42",
            $"received MessageDeduplicationId {got.Attributes.GetValueOrDefault("MessageDeduplicationId") ?? "None"}");
        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = got.ReceiptHandle });

        var retry = await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = url,
            MessageBody = "charge-order-42-retry",
            MessageGroupId = "orders",
            MessageDeduplicationId = "order-42",
        });
        Ensure(retry.MessageId == first.MessageId,
            "a retry inside the dedup window must be acknowledged with the original MessageId, even " +
            "after the original was consumed and deleted; the ids differ");
        Ensure(Key(retry.SequenceNumber, "SequenceNumber") == Key(first.SequenceNumber, "SequenceNumber"),
            "a retry inside the dedup window must echo the original SequenceNumber; got " +
            $"{retry.SequenceNumber} vs {first.SequenceNumber}");

        var fresh = await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = url,
            MessageBody = "charge-order-43",
            MessageGroupId = "orders",
            MessageDeduplicationId = "order-43",
        });
        Ensure(BigInteger.Parse(Key(fresh.SequenceNumber, "SequenceNumber")) > BigInteger.Parse(Key(first.SequenceNumber, "SequenceNumber")),
            $"a new deduplication id must get a later SequenceNumber: {fresh.SequenceNumber} " +
            $"after {first.SequenceNumber}");

        var bodies = new List<string>();
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed.TotalSeconds < 20)
        {
            var msgs = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 })).Messages ?? [];
            if (msgs.Count == 0 && bodies.Count > 0)
            {
                break;
            }

            foreach (var m in msgs)
            {
                bodies.Add(m.Body);
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle });
            }
        }

        Ensure(bodies.SequenceEqual(["charge-order-43"]),
            "after the consumed message, only the new deduplication id may be delivered -- never the " +
            $"retry; received [{string.Join(", ", bodies)}]");
    }

    private static async Task<Message?> ReceiveOne(AmazonSQSClient sqs, string url, double timeoutSeconds)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed.TotalSeconds < timeoutSeconds)
        {
            var msgs = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url,
                MaxNumberOfMessages = 1,
                WaitTimeSeconds = 2,
                MessageSystemAttributeNames = ["MessageDeduplicationId", "SequenceNumber"],
            })).Messages ?? [];
            if (msgs.Count > 0)
            {
                return msgs[0];
            }
        }

        return null;
    }

    /// <summary>Python's <c>resp["Key"]</c>: a <see cref="KeyNotFoundException"/> when the response omitted it.</summary>
    private static T Key<T>(T? value, string name)
        where T : class
        => value ?? throw new KeyNotFoundException(name);
}

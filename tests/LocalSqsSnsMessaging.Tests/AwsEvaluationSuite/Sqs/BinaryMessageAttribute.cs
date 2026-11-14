// Translated from behavioral/sqs/check_binary_message_attribute.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.binary-message-attribute</summary>
[Category("AwsEvaluationSuite")]
public sealed class BinaryMessageAttribute
{
    private const string Check = "binary-message-attribute";

    /// <summary>
    /// Binary MessageAttribute (DataType=Binary) round-trips byte-exact.
    /// </summary>
    [Test]
    [DisplayName("sqs.binary-message-attribute")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-"),
            Tags = new() { ["sim2real-check"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var payload = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        var sent = new Dictionary<string, MessageAttributeValue>
        {
            ["blob"] = new() { DataType = "Binary", BinaryValue = new MemoryStream(payload) },
        };
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "binary-attr", MessageAttributes = sent });
        var received = new List<Message>();
        var seen = new HashSet<string>();
        var idle = 0;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 1, MessageAttributeNames = ["All"],
            });
            var msgs = resp.Messages ?? [];
            if (msgs.Count == 0)
            {
                idle++;
                if (received.Count > 0 && idle >= 3)
                {
                    break;
                }

                continue;
            }

            idle = 0;
            foreach (var m in msgs)
            {
                if (seen.Add(m.MessageId))
                {
                    received.Add(m);
                }

                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle });
            }

            if (received.Count >= 1)
            {
                break;
            }
        }

        Ensure(received.Count > 0, "no message received");
        var got = received[0].MessageAttributes?.GetValueOrDefault("blob");
        Ensure(got is not null, "binary attribute missing on receive");
        Ensure(got.DataType == "Binary", $"DataType mismatch: '{got.DataType}'");
        Ensure(got.BinaryValue is not null && got.BinaryValue.ToArray().SequenceEqual(payload), "BinaryValue did not round-trip byte-exact");
    }
}

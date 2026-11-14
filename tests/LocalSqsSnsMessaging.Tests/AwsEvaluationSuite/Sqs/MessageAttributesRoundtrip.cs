// Translated from behavioral/sqs/check_message_attributes_roundtrip.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Text;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.message-attributes-roundtrip</summary>
[Category("AwsEvaluationSuite")]
public sealed class MessageAttributesRoundtrip
{
    private const string Check = "message-attributes-roundtrip";

    // Python's random.Random(0x5EED) sequence can't be reproduced in .NET; a seeded System.Random
    // draws from the same alphabet instead.
    private static readonly string[] SafeChars =
    [
        .. Enumerable.Range(0x20, 0x7F - 0x20).Select(c => ((char)c).ToString()),
        "\t", "\n", "\r",
        .. "áéíóúñüßçΩλµ§€—…".Select(c => c.ToString()),
        .. "日本語中文한국어".Select(c => c.ToString()),
        "😀", "🚀", "✅", "🌍",
    ];

    private static string RandomBody(Random rng, int nChars)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < nChars; i++)
        {
            sb.Append(SafeChars[rng.Next(SafeChars.Length)]);
        }

        return sb.ToString();
    }

    /// <summary>
    /// String + Number MessageAttributes round-trip correctly.
    /// </summary>
    [Test]
    [DisplayName("sqs.message-attributes-roundtrip")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var rng = new Random(0x5EED);
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-"),
            Tags = new() { ["sim2real-check"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var stringValue = RandomBody(rng, 24).Replace("\n", " ", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\t", " ", StringComparison.Ordinal);
        var numberValue = rng.Next(0, 10_000_000 + 1).ToString(CultureInfo.InvariantCulture);
        var sent = new Dictionary<string, MessageAttributeValue>
        {
            ["flavour"] = new() { DataType = "String", StringValue = stringValue },
            ["priority"] = new() { DataType = "Number", StringValue = numberValue },
        };
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "attrs-probe", MessageAttributes = sent });
        var received = new List<Message>();
        var seen = new HashSet<string>();
        var idle = 0;
        for (var i = 0; i < 12; i++)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url,
                MaxNumberOfMessages = 1,
                WaitTimeSeconds = 1,
                MessageAttributeNames = ["All"],
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
        var got = received[0].MessageAttributes;
        Ensure(got is { Count: > 0 }, "no MessageAttributes on receive");
        foreach (var (key, spec) in sent)
        {
            Ensure(got.ContainsKey(key),
                $"attribute '{key}' missing from received MessageAttributes [{string.Join(", ", got.Keys.Order(StringComparer.Ordinal))}]");
            Ensure(got[key].DataType == spec.DataType,
                $"attribute '{key}' DataType mismatch: expected '{spec.DataType}', got '{got[key].DataType}'");
            Ensure(got[key].StringValue == spec.StringValue,
                $"attribute '{key}' StringValue mismatch: expected '{spec.StringValue}', got '{got[key].StringValue}'");
        }
    }
}

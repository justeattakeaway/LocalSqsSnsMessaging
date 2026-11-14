// Translated from behavioral/sqs/check_md5_of_message_attributes.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.md5-of-message-attributes</summary>
[Category("AwsEvaluationSuite")]
public sealed class Md5OfMessageAttributes
{
    private const string Check = "md5-of-message-attributes";

    /// <summary>
    /// SendMessage's MD5OfMessageAttributes equals a locally recomputed digest of SQS's documented
    /// attribute encoding, and the received message carries the same value.
    /// </summary>
    [Test]
    [DisplayName("sqs.md5-of-message-attributes")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.MessageAttributesMd5, "MD5OfMessageAttributes on send: expected 'aa235556208daf7d7b07d0775d52dd74' (SQS's own digest algorithm), got None")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-"), Tags = new() { ["sim2real"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var attrs = new Dictionary<string, MessageAttributeValue>
        {
            ["flavour"] = new() { DataType = "String", StringValue = "vanilla" },
            ["priority"] = new() { DataType = "Number", StringValue = "7" },
        };
        var expectedMd5 = ExpectedMd5OfMessageAttributes(attrs);
        var resp = await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "md5-attrs", MessageAttributes = attrs });
        var sentMd5 = resp.MD5OfMessageAttributes;
        Ensure(sentMd5 == expectedMd5,
            $"MD5OfMessageAttributes on send: expected '{expectedMd5}' (SQS's own digest " +
            $"algorithm over {Repr(attrs)}), got '{sentMd5 ?? "None"}'");
        List<Message> received = [];
        HashSet<string> seen = [];
        var idle = 0;
        for (var r = 0; r < 12; r++)
        {
            var rr = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 1, MessageAttributeNames = ["All"] });
            var msgs = rr.Messages ?? [];
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

        Ensure(received.Count > 0, "expected at least 1 message from receive_message, got none");
        var gotMd5 = received[0].MD5OfMessageAttributes;
        Ensure(gotMd5 == expectedMd5,
            $"MD5OfMessageAttributes on receive: expected '{expectedMd5}' (send returned " +
            $"'{sentMd5}'), got '{gotMd5 ?? "None"}'");
    }

    /// <summary>
    /// SQS's documented digest: per attribute sorted by name, length-prefixed name and data type,
    /// a transport byte (1 = string/number, 2 = binary), then the length-prefixed value.
    /// Lengths are 4-byte big-endian; text is UTF-8.
    /// </summary>
    private static string ExpectedMd5OfMessageAttributes(Dictionary<string, MessageAttributeValue> attrs)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        foreach (var name in attrs.Keys.Order(StringComparer.Ordinal))
        {
            var spec = attrs[name];
            var nameBytes = Encoding.UTF8.GetBytes(name);
            h.AppendData(PackUInt32(nameBytes.Length));
            h.AppendData(nameBytes);
            var dataType = spec.DataType;
            var typeBytes = Encoding.UTF8.GetBytes(dataType);
            h.AppendData(PackUInt32(typeBytes.Length));
            h.AppendData(typeBytes);
            byte[] valueBytes;
            if (dataType.StartsWith("Binary", StringComparison.Ordinal))
            {
                valueBytes = spec.BinaryValue.ToArray();
                h.AppendData([0x02]);
            }
            else
            {
                valueBytes = Encoding.UTF8.GetBytes(spec.StringValue);
                h.AppendData([0x01]);
            }

            h.AppendData(PackUInt32(valueBytes.Length));
            h.AppendData(valueBytes);
        }

        return Convert.ToHexStringLower(h.GetHashAndReset());
    }

    /// <summary><c>struct.pack("&gt;I", n)</c>.</summary>
    private static byte[] PackUInt32(int n)
    {
        var buffer = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, checked((uint)n));
        return buffer;
    }

    private static string Repr(Dictionary<string, MessageAttributeValue> attrs) =>
        "{" + string.Join(", ", attrs.Select(kv => $"'{kv.Key}': {{'DataType': '{kv.Value.DataType}', 'StringValue': '{kv.Value.StringValue}'}}")) + "}";
}

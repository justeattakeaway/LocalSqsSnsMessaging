// Translated from behavioral/sqs/check_fifo_message_attribute_digest_sensitivity_across_types.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Amazon.SQS;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.fifo-message-attribute-digest-sensitivity-across-types</summary>
[Category("AwsEvaluationSuite")]
public sealed class FifoMessageAttributeDigestSensitivityAcrossTypes
{
    private sealed record Attr(string DataType, string? StringValue = null, byte[]? BinaryValue = null);

    /// <summary>
    /// On a FIFO queue, SendMessage with an explicit MessageDeduplicationId and String, Number,
    /// Binary and custom-typed attributes returns digests matching a local recomputation, which
    /// ReceiveMessage reproduces byte-exact; flipping one Binary byte changes the digest to match.
    /// </summary>
    [Test]
    [DisplayName("sqs.fifo-message-attribute-digest-sensitivity-across-types")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-") + ".fifo",
            Attributes = new() { ["FifoQueue"] = "true", ["ContentBasedDeduplication"] = "false" },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var token1 = Guid.NewGuid().ToString("N")[..8];
        var body1 = $"fifo-digest-{token1}";
        byte[] binaryValue1 = [0x10, 0x20, 0x30, 0x40, 0x50];
        var attrs1 = new Dictionary<string, Attr>
        {
            ["str-attr"] = new("String", StringValue: $"plain-{token1}"),
            ["num-attr"] = new("Number", StringValue: "123456"),
            ["bin-attr"] = new("Binary", BinaryValue: binaryValue1),
            ["custom-attr"] = new("String.custom", StringValue: $"custom-{token1}"),
        };
        var wantMd51 = AttributesDigest(attrs1);
        var wantBodyMd51 = Md5(body1);
        var sent1 = await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = url, MessageBody = body1, MessageGroupId = "g1", MessageDeduplicationId = $"dedup-{token1}",
            MessageAttributes = ToMessageAttributes(attrs1),
        });
        Ensure(sent1.MD5OfMessageBody == wantBodyMd51,
            $"MD5OfMessageBody on the first FIFO send was '{sent1.MD5OfMessageBody}', expected md5(body) '{wantBodyMd51}'");
        Ensure(sent1.MD5OfMessageAttributes == wantMd51,
            $"MD5OfMessageAttributes on the first FIFO send was '{sent1.MD5OfMessageAttributes}', expected the recomputed digest '{wantMd51}'");

        // Second send: a new MessageDeduplicationId (so it is not treated as a replay of the first),
        // with one byte of the Binary value flipped and every other attribute unchanged.
        var token2 = Guid.NewGuid().ToString("N")[..8];
        var body2 = $"fifo-digest-{token2}";
        byte[] binaryValue2 = [0xFF, .. binaryValue1[1..]];
        Ensure(binaryValue2.Length == binaryValue1.Length,
            "the perturbation must preserve the value length so this leg proves byte sensitivity, not a length change");
        var attrs2 = new Dictionary<string, Attr>(attrs1) { ["bin-attr"] = new("Binary", BinaryValue: binaryValue2) };
        var wantMd52 = AttributesDigest(attrs2);
        Ensure(wantMd52 != wantMd51,
            "the local recomputation is insensitive to a one-byte Binary value change, so this leg " +
            $"cannot discriminate: both digests are '{wantMd51}'");
        var wantBodyMd52 = Md5(body2);
        var sent2 = await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = url, MessageBody = body2, MessageGroupId = "g1", MessageDeduplicationId = $"dedup-{token2}",
            MessageAttributes = ToMessageAttributes(attrs2),
        });
        Ensure(sent2.MD5OfMessageBody == wantBodyMd52,
            $"MD5OfMessageBody on the second FIFO send was '{sent2.MD5OfMessageBody}', expected md5(body) '{wantBodyMd52}'");
        Ensure(sent2.MD5OfMessageAttributes == wantMd52,
            $"MD5OfMessageAttributes after flipping one Binary byte was '{sent2.MD5OfMessageAttributes}', " +
            $"expected the newly recomputed digest '{wantMd52}' (it was '{wantMd51}' before the flip)");

        // Independent read: both messages, through a different operation than SendMessage.
        var received = await ReceiveByToken(sqs, url, [token1, token2], budgetSeconds: 40.0);
        var msg1 = received[token1];
        Ensure(msg1.Body == body1, $"delivered body '{msg1.Body}' != sent '{body1}'");
        Ensure(msg1.MD5OfBody == wantBodyMd51,
            $"ReceiveMessage's MD5OfBody for the first message was '{msg1.MD5OfBody}', expected '{wantBodyMd51}'");
        Ensure(msg1.MD5OfMessageAttributes == wantMd51,
            $"ReceiveMessage's MD5OfMessageAttributes for the first message was '{msg1.MD5OfMessageAttributes}', expected the recomputed digest '{wantMd51}'");
        var got1 = msg1.MessageAttributes ?? [];
        Ensure(got1.Keys.ToHashSet().SetEquals(attrs1.Keys),
            $"received attribute names [{string.Join(", ", got1.Keys.Order(StringComparer.Ordinal))}] != sent [{string.Join(", ", attrs1.Keys.Order(StringComparer.Ordinal))}]");
        Ensure(got1["str-attr"].DataType == "String", $"String attribute DataType was '{got1["str-attr"].DataType}'");
        Ensure(got1["str-attr"].StringValue == attrs1["str-attr"].StringValue,
            $"String attribute value '{got1["str-attr"].StringValue}' != sent '{attrs1["str-attr"].StringValue}'");
        Ensure(got1["num-attr"].DataType == "Number", $"Number attribute DataType was '{got1["num-attr"].DataType}'");
        Ensure(got1["num-attr"].StringValue == "123456", $"Number attribute value '{got1["num-attr"].StringValue}' != sent '123456'");
        Ensure(got1["bin-attr"].DataType == "Binary", $"Binary attribute DataType was '{got1["bin-attr"].DataType}'");
        var gotBin1 = got1["bin-attr"].BinaryValue?.ToArray() ?? [];
        Ensure(gotBin1.SequenceEqual(binaryValue1),
            $"Binary attribute value {Convert.ToHexString(gotBin1)} != sent {Convert.ToHexString(binaryValue1)}");
        Ensure(got1["custom-attr"].DataType == "String.custom",
            $"custom attribute DataType '{got1["custom-attr"].DataType}' != sent 'String.custom'");
        Ensure(got1["custom-attr"].StringValue == attrs1["custom-attr"].StringValue,
            $"custom attribute value '{got1["custom-attr"].StringValue}' != sent '{attrs1["custom-attr"].StringValue}'");
        await sqs.DeleteMessageAsync(url, msg1.ReceiptHandle);

        var msg2 = received[token2];
        Ensure(msg2.Body == body2, $"delivered body '{msg2.Body}' != sent '{body2}'");
        Ensure(msg2.MD5OfMessageAttributes == wantMd52,
            $"ReceiveMessage's MD5OfMessageAttributes for the second message was '{msg2.MD5OfMessageAttributes}', expected the recomputed digest '{wantMd52}'");
        var got2Bin = msg2.MessageAttributes?.GetValueOrDefault("bin-attr");
        Ensure(got2Bin?.DataType == "Binary", $"Binary attribute DataType was '{got2Bin?.DataType}'");
        var got2BinBytes = got2Bin?.BinaryValue?.ToArray() ?? [];
        Ensure(got2BinBytes.SequenceEqual(binaryValue2),
            $"Binary attribute value {Convert.ToHexString(got2BinBytes)} != the perturbed {Convert.ToHexString(binaryValue2)}");
        await sqs.DeleteMessageAsync(url, msg2.ReceiptHandle);
    }

    private static string Md5(string s) => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(s)));

    private static byte[] LengthPrefixed(byte[] raw)
    {
        var buf = new byte[4 + raw.Length];
        BinaryPrimitives.WriteInt32BigEndian(buf, raw.Length);
        raw.CopyTo(buf, 4);
        return buf;
    }

    private static byte[] EncodeAttribute(string name, Attr attr)
    {
        var buf = new List<byte>();
        buf.AddRange(LengthPrefixed(Encoding.UTF8.GetBytes(name)));
        buf.AddRange(LengthPrefixed(Encoding.UTF8.GetBytes(attr.DataType)));
        if (attr.BinaryValue is not null)
        {
            buf.Add(0x02);
            buf.AddRange(LengthPrefixed(attr.BinaryValue));
        }
        else
        {
            buf.Add(0x01);
            buf.AddRange(LengthPrefixed(Encoding.UTF8.GetBytes(attr.StringValue!)));
        }

        return [.. buf];
    }

    /// <summary>The documented per-attribute, name-sorted MD5 over DataType and value.</summary>
    private static string AttributesDigest(Dictionary<string, Attr> attrs)
    {
        var buf = attrs.Keys.Order(StringComparer.Ordinal).SelectMany(n => EncodeAttribute(n, attrs[n])).ToArray();
        return Convert.ToHexStringLower(MD5.HashData(buf));
    }

    private static Dictionary<string, MessageAttributeValue> ToMessageAttributes(Dictionary<string, Attr> attrs) =>
        attrs.ToDictionary(kv => kv.Key, kv => new MessageAttributeValue
        {
            DataType = kv.Value.DataType,
            StringValue = kv.Value.StringValue,
            BinaryValue = kv.Value.BinaryValue is { } bytes ? new MemoryStream(bytes) : null,
        });

    /// <summary>Bounded poll collecting one FIFO message per body token in <paramref name="tokens"/>.</summary>
    private static async Task<Dictionary<string, Message>> ReceiveByToken(AmazonSQSClient sqs, string url, HashSet<string> tokens, double budgetSeconds)
    {
        var deadline = FidelityTime.StartNew();
        var found = new Dictionary<string, Message>();
        while (deadline.Elapsed < TimeSpan.FromSeconds(budgetSeconds) && found.Count < tokens.Count)
        {
            var msgs = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2, MessageAttributeNames = ["All"],
            })).Messages ?? [];
            foreach (var msg in msgs)
            {
                var matched = tokens.FirstOrDefault(t => msg.Body.Contains(t, StringComparison.Ordinal));
                if (matched is not null && !found.ContainsKey(matched))
                {
                    found[matched] = msg;
                }
                else if (matched is null)
                {
                    await sqs.ChangeMessageVisibilityAsync(url, msg.ReceiptHandle, 0);
                }
            }
        }

        var missing = tokens.Except(found.Keys).Order(StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
        {
            throw Fail($"no message carrying token(s) [{string.Join(", ", missing)}] arrived within {budgetSeconds}s");
        }

        return found;
    }
}

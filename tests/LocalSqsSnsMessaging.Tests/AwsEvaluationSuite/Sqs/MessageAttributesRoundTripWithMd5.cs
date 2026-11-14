// Translated from behavioral/sqs/check_message_attributes_round_trip_with_md5.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.message-attributes-round-trip-with-md5</summary>
[Category("AwsEvaluationSuite")]
public sealed class MessageAttributesRoundTripWithMd5
{
    /// <summary>
    /// String, Number and Binary attributes on a standard queue round-trip byte-exact, and
    /// MD5OfMessageAttributes on send and receive equals the documented canonical digest.
    /// </summary>
    [Test]
    [DisplayName("sqs.message-attributes-round-trip-with-md5")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.MessageAttributesMd5, "MD5OfMessageAttributes on send is None, expected the canonical digest '449eb38d855e790ba68995ae92338d6c'")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        byte[] binary = [0x00, 0x01, 0xff, 0xfe];
        var sent = new Dictionary<string, MessageAttributeValue>
        {
            ["StringAttr"] = new() { DataType = "String", StringValue = "hello-world" },
            ["NumberAttr"] = new() { DataType = "Number", StringValue = "42" },
            ["BinaryAttr"] = new() { DataType = "Binary", BinaryValue = new MemoryStream(binary) },
        };
        var sendResp = await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "attrs-md5-round-trip", MessageAttributes = sent });
        var sentMd5 = sendResp.MD5OfMessageAttributes;
        var expectedMd5 = AttributeDigest(sent);
        Ensure(sentMd5 == expectedMd5,
            $"MD5OfMessageAttributes on send is '{sentMd5}', expected the canonical digest '{expectedMd5}'");

        var msg = await PollReceiveOne(sqs, url, timeoutSeconds: 20.0);
        Ensure(msg.Body == "attrs-md5-round-trip", $"body mismatch: '{msg.Body}'");

        var received = msg.MessageAttributes;
        if (received is null or { Count: 0 })
        {
            throw Fail("no MessageAttributes on receive");
        }

        Ensure(received["StringAttr"].DataType == "String", Describe(received["StringAttr"]));
        Ensure(received["StringAttr"].StringValue == "hello-world", Describe(received["StringAttr"]));
        Ensure(received["NumberAttr"].DataType == "Number", Describe(received["NumberAttr"]));
        Ensure(received["NumberAttr"].StringValue == "42", Describe(received["NumberAttr"]));
        Ensure(received["BinaryAttr"].DataType == "Binary", Describe(received["BinaryAttr"]));
        Ensure(received["BinaryAttr"].BinaryValue!.ToArray().AsSpan().SequenceEqual(binary), Describe(received["BinaryAttr"]));

        var gotMd5 = msg.MD5OfMessageAttributes;
        if (gotMd5 != sentMd5)
        {
            throw Fail($"MD5OfMessageAttributes did not round-trip: send '{sentMd5}' != recv '{gotMd5}'");
        }

        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle });
    }

    private static string Describe(MessageAttributeValue v)
        => $"{{DataType: {v.DataType}, StringValue: {v.StringValue}, BinaryValue: {(v.BinaryValue is null ? "None" : Convert.ToHexStringLower(v.BinaryValue.ToArray()))}}}";

    private static byte[] LengthPrefixed(byte[] data)
    {
        var buf = new byte[4 + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(buf, (uint)data.Length);
        data.CopyTo(buf, 4);
        return buf;
    }

    /// <summary>Encode one message attribute as the SQS Developer Guide specifies.</summary>
    private static byte[] EncodeOneAttribute(string name, MessageAttributeValue definition)
    {
        var buf = new List<byte>();
        buf.AddRange(LengthPrefixed(Encoding.UTF8.GetBytes(name)));
        buf.AddRange(LengthPrefixed(Encoding.UTF8.GetBytes(definition.DataType)));
        if (definition.StringValue is not null)
        {
            buf.Add(0x01);
            buf.AddRange(LengthPrefixed(Encoding.UTF8.GetBytes(definition.StringValue)));
            return [.. buf];
        }

        buf.Add(0x02);
        buf.AddRange(LengthPrefixed(definition.BinaryValue!.ToArray()));
        return [.. buf];
    }

    /// <summary>The documented MD5 over a whole attribute map: sort by name, concatenate, hash once.</summary>
    private static string AttributeDigest(Dictionary<string, MessageAttributeValue> attributes)
    {
        var buf = attributes.Keys.Order(StringComparer.Ordinal).SelectMany(n => EncodeOneAttribute(n, attributes[n])).ToArray();
        return Convert.ToHexStringLower(MD5.HashData(buf));
    }

    private static async Task<Message> PollReceiveOne(AmazonSQSClient sqs, string url, double timeoutSeconds = 20.0)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url,
                MaxNumberOfMessages = 1,
                WaitTimeSeconds = 3,
                MessageAttributeNames = ["All"],
            });
            if (resp.Messages is { Count: > 0 } messages)
            {
                return messages[0];
            }
        }

        throw Fail($"no message received within {timeoutSeconds}s");
    }
}

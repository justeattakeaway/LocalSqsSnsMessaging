// Translated from behavioral/sqs/check_message_digests_recomputed_from_the_documented_canonical_encoding.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.message-digests-recomputed-from-the-documented-canonical-encoding</summary>
[Category("AwsEvaluationSuite")]
public sealed class MessageDigestsRecomputedFromTheDocumentedCanonicalEncoding
{
    // A well-formed X-Amzn-Trace-Id value; AWSTraceHeader is the only documented system attribute.
    private const string TraceHeader = "Root=1-5759e988-bd862e3fe1be46a994272793;Parent=53995c3f42cd8ad8;Sampled=1";

    private static Dictionary<string, string> Tags => new()
    {
        ["sim2real:managed"] = "true",
        ["sim2real"] = "message-digests-recomputed",
    };

    /// <summary>One attribute definition as the Python dicts carry it (StringValue or BinaryValue).</summary>
    private sealed record Attr(string DataType, string? StringValue = null, byte[]? BinaryValue = null);

    /// <summary>4-byte big-endian length, then the raw bytes -- the guide's only framing primitive.</summary>
    private static byte[] LengthPrefixed(byte[] raw)
    {
        var buf = new byte[4 + raw.Length];
        BinaryPrimitives.WriteUInt32BigEndian(buf, (uint)raw.Length);
        raw.CopyTo(buf, 4);
        return buf;
    }

    /// <summary>Encode a single message attribute exactly as the SQS Developer Guide specifies.</summary>
    private static byte[] EncodeOneAttribute(string name, Attr definition)
    {
        var buf = new List<byte>();
        buf.AddRange(LengthPrefixed(Encoding.UTF8.GetBytes(name)));
        buf.AddRange(LengthPrefixed(Encoding.UTF8.GetBytes(definition.DataType)));
        if (definition.StringValue is not null)
        {
            // Logical String AND Number both travel on the String transport type -> byte 1.
            buf.Add(0x01);
            buf.AddRange(LengthPrefixed(Encoding.UTF8.GetBytes(definition.StringValue)));
        }
        else if (definition.BinaryValue is not null)
        {
            buf.Add(0x02);
            buf.AddRange(LengthPrefixed(definition.BinaryValue));
        }
        else
        {
            throw Fail($"attribute '{name}' carries neither StringValue nor BinaryValue, so this check cannot encode it");
        }

        return [.. buf];
    }

    /// <summary>The documented MD5 over a whole attribute map: sort by name, concatenate, hash once.</summary>
    private static string AttributeDigest(Dictionary<string, Attr> attributes)
    {
        var buf = attributes.Keys.Order(StringComparer.Ordinal).SelectMany(n => EncodeOneAttribute(n, attributes[n])).ToArray();
        return Convert.ToHexStringLower(MD5.HashData(buf));
    }

    private static Dictionary<string, MessageAttributeValue> ToMessageAttributes(Dictionary<string, Attr> attributes)
        => attributes.ToDictionary(
            kv => kv.Key,
            kv => new MessageAttributeValue
            {
                DataType = kv.Value.DataType,
                StringValue = kv.Value.StringValue,
                BinaryValue = kv.Value.BinaryValue is null ? null : new MemoryStream(kv.Value.BinaryValue),
            });

    private static Dictionary<string, MessageSystemAttributeValue> ToSystemAttributes(Dictionary<string, Attr> attributes)
        => attributes.ToDictionary(
            kv => kv.Key,
            kv => new MessageSystemAttributeValue
            {
                DataType = kv.Value.DataType,
                StringValue = kv.Value.StringValue,
                BinaryValue = kv.Value.BinaryValue is null ? null : new MemoryStream(kv.Value.BinaryValue),
            });

    private static string Sorted(IEnumerable<string> names) => "[" + string.Join(", ", names.Order(StringComparer.Ordinal).Select(n => $"'{n}'")) + "]";

    private static string Token() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// Bounded poll for the message whose body carries `token`; raises on the deadline.
    /// A standard queue's first receive can miss a message; others are re-exposed with visibility 0.
    /// </summary>
    private static async Task<Message> ReceiveOne(
        AmazonSQSClient sqs, string url, string token, double timeoutSeconds,
        List<string>? messageAttributeNames = null, List<string>? messageSystemAttributeNames = null)
    {
        var deadline = Stopwatch.StartNew();
        var seen = new List<string>();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            var request = new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 };
            if (messageAttributeNames is not null)
            {
                request.MessageAttributeNames = messageAttributeNames;
            }

            if (messageSystemAttributeNames is not null)
            {
                request.MessageSystemAttributeNames = messageSystemAttributeNames;
            }

            foreach (var msg in (await sqs.ReceiveMessageAsync(request)).Messages ?? [])
            {
                if (msg.Body.Contains(token, StringComparison.Ordinal))
                {
                    return msg;
                }

                seen.Add(msg.Body);
                await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest
                {
                    QueueUrl = url,
                    ReceiptHandle = msg.ReceiptHandle,
                    VisibilityTimeout = 0,
                });
            }
        }

        throw Fail($"no message containing '{token}' arrived within {timeoutSeconds}s; bodies seen: [{string.Join(", ", seen)}]");
    }

    /// <summary>
    /// SendMessage's body, attribute and system-attribute MD5s, and ReceiveMessage's, equal a local
    /// recomputation of the documented encoding; the attribute digest ignores insertion order and
    /// changes when one value byte changes.
    ///
    /// Reference: https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/sqs-message-metadata.html
    /// </summary>
    [Test]
    [DisplayName("sqs.message-digests-recomputed-from-the-documented-canonical-encoding")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.MessageAttributesMd5, "MD5OfMessageAttributes does not match the documented canonical encoding of ['alpha', 'mid', 'zeta']: expected 215db4f5bea33de9ca671a922f158f57, got None")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-"), Tags = Tags })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        const double pollS = 40.0;

        var token = Token();
        var body = $"digest-subject-{token}";

        // Names chosen so that insertion order (zeta, alpha, mid) and sorted order
        // (alpha, mid, zeta) disagree, and so that leg (c) has something to prove. The custom
        // suffixes are deliberate: the guide requires them INSIDE the digest.
        var attributes = new Dictionary<string, Attr>
        {
            ["zeta"] = new("String.urn", StringValue: $"urn:sim2real:{token}"),
            ["alpha"] = new("Number", StringValue: "7"),
            ["mid"] = new("Binary.gzip", BinaryValue: Enumerable.Range(0, 8).Select(i => (byte)i).ToArray()),
        };
        var systemAttributes = new Dictionary<string, Attr> { ["AWSTraceHeader"] = new("String", StringValue: TraceHeader) };

        var wantBodyMd5 = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(body)));
        var wantAttrMd5 = AttributeDigest(attributes);
        var wantSysMd5 = AttributeDigest(systemAttributes);

        var sent = await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = url,
            MessageBody = body,
            MessageAttributes = ToMessageAttributes(attributes),
            MessageSystemAttributes = ToSystemAttributes(systemAttributes),
        });

        // --- (a) SendMessage's digests are the documented function of the payload ----------
        Ensure(sent.MD5OfMessageBody == wantBodyMd5,
            $"MD5OfMessageBody is not md5(body UTF-8 bytes): expected {wantBodyMd5}, got " +
            $"'{sent.MD5OfMessageBody}' for a {Encoding.UTF8.GetByteCount(body)}-byte body");
        Ensure(sent.MD5OfMessageAttributes == wantAttrMd5,
            $"MD5OfMessageAttributes does not match the documented canonical encoding of " +
            $"{Sorted(attributes.Keys)}: expected {wantAttrMd5}, got '{sent.MD5OfMessageAttributes}'");
        Ensure(sent.MD5OfMessageSystemAttributes == wantSysMd5,
            $"MD5OfMessageSystemAttributes does not match the documented canonical encoding of " +
            $"{Sorted(systemAttributes.Keys)}: expected {wantSysMd5}, got '{sent.MD5OfMessageSystemAttributes}'");

        // --- (b) independent read at a different operation reproduces them ----------------
        var got = await ReceiveOne(sqs, url, token, pollS, messageAttributeNames: ["All"], messageSystemAttributeNames: ["All"]);
        // "MD5OfBody" on a RECEIVED message, not "MD5OfMessageBody": the two operations name the same
        // digest differently, and only SendMessage's response carries MD5OfMessageBody.
        Ensure(got.MD5OfBody == wantBodyMd5,
            $"ReceiveMessage's MD5OfBody disagrees with the recomputed digest: expected {wantBodyMd5}, got '{got.MD5OfBody}'");
        Ensure(got.MD5OfMessageAttributes == wantAttrMd5,
            $"ReceiveMessage's MD5OfMessageAttributes disagrees with the recomputed digest over the " +
            $"full projection: expected {wantAttrMd5}, got '{got.MD5OfMessageAttributes}'");
        var receivedAttrs = got.MessageAttributes!;
        Ensure(receivedAttrs.Keys.ToHashSet().SetEquals(attributes.Keys),
            $"the round-tripped attribute NAMES differ: sent {Sorted(attributes.Keys)}, got {Sorted(receivedAttrs.Keys)}");
        Ensure(receivedAttrs["zeta"].DataType == "String.urn",
            $"the custom DataType suffix was not preserved: got '{receivedAttrs["zeta"].DataType}', expected 'String.urn'");
        Ensure(receivedAttrs["mid"].DataType == "Binary.gzip",
            $"the custom Binary DataType suffix was not preserved: got '{receivedAttrs["mid"].DataType}', expected 'Binary.gzip'");
        var midBytes = receivedAttrs["mid"].BinaryValue!.ToArray();
        Ensure(midBytes.AsSpan().SequenceEqual(attributes["mid"].BinaryValue),
            $"the Binary attribute value did not round-trip byte-exactly: sent " +
            $"{Convert.ToHexStringLower(attributes["mid"].BinaryValue!)}, got {Convert.ToHexStringLower(midBytes)}");
        Ensure(receivedAttrs["alpha"].StringValue == "7",
            $"a Number attribute travels as StringValue '7'; got '{receivedAttrs["alpha"].StringValue}'");
        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = got.ReceiptHandle });

        // --- (c) the digest is over the SORTED canonical form, not the wire order ---------
        var reversedAttributes = attributes.Keys.Reverse().ToDictionary(name => name, name => attributes[name]);
        Ensure(!reversedAttributes.Keys.SequenceEqual(attributes.Keys),
            "the reversed-order probe is only meaningful if the two insertion orders differ; they " +
            $"are both [{string.Join(", ", attributes.Keys)}]");
        var tokenC = Token();
        var bodyC = $"digest-order-{tokenC}";
        var sentC = await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = url,
            MessageBody = bodyC,
            MessageAttributes = ToMessageAttributes(reversedAttributes),
        });
        Ensure(sentC.MD5OfMessageAttributes == wantAttrMd5,
            $"MD5OfMessageAttributes changed when the identical attribute map was sent in reversed " +
            $"insertion order [{string.Join(", ", reversedAttributes.Keys)}]: expected the sorted-canonical digest " +
            $"{wantAttrMd5}, got '{sentC.MD5OfMessageAttributes}'");

        // --- (d) sensitivity: one byte of one value, same names/types/lengths -------------
        var perturbed = attributes.ToDictionary(kv => kv.Key, kv => kv.Value with { });
        perturbed["mid"] = perturbed["mid"] with { BinaryValue = [0xFF, .. attributes["mid"].BinaryValue![1..]] };
        Ensure(perturbed["mid"].BinaryValue!.Length == attributes["mid"].BinaryValue!.Length,
            "the perturbation must preserve the value LENGTH, otherwise a length-only digest would " +
            "also change and the leg proves nothing");
        var wantPerturbedMd5 = AttributeDigest(perturbed);
        Ensure(wantPerturbedMd5 != wantAttrMd5,
            "the local recomputation is insensitive to a one-byte value change, so this leg cannot " +
            $"discriminate: both digests are {wantAttrMd5}");
        var tokenD = Token();
        var bodyD = $"digest-sensitivity-{tokenD}";
        var sentD = await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = url,
            MessageBody = bodyD,
            MessageAttributes = ToMessageAttributes(perturbed),
        });
        Ensure(sentD.MD5OfMessageAttributes == wantPerturbedMd5,
            $"flipping one byte of one Binary value must yield the recomputed digest " +
            $"{wantPerturbedMd5} (it was {wantAttrMd5} before the flip); got '{sentD.MD5OfMessageAttributes}'");

        // Terminal read: the two extra messages are drained through a different operation than the
        // SendMessage that created them, and their digests are re-read at the destination.
        foreach (var (wantToken, wantMd5) in new[] { (tokenC, wantAttrMd5), (tokenD, wantPerturbedMd5) })
        {
            var msg = await ReceiveOne(sqs, url, wantToken, pollS, messageAttributeNames: ["All"]);
            Ensure(msg.MD5OfMessageAttributes == wantMd5,
                $"the receive-side attribute digest for {wantToken} was '{msg.MD5OfMessageAttributes}', expected the recomputed {wantMd5}");
            await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle });
        }
    }
}

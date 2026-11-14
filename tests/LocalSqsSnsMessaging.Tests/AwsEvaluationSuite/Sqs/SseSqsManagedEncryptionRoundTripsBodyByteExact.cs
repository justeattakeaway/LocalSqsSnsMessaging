// Translated from behavioral/sqs/check_sse_sqs_managed_encryption_round_trips_body_byte_exact.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.sse-sqs-managed-encryption-round-trips-body-byte-exact</summary>
[Category("AwsEvaluationSuite")]
public sealed class SseSqsManagedEncryptionRoundTripsBodyByteExact
{
    private static Dictionary<string, string> Tags() => new() { ["sim2real:managed"] = "true", ["sim2real"] = "sse-sqs-managed-roundtrip" };

    // Deliberately awkward but entirely SQS-LEGAL: the allowed set is #x9 | #xA | #xD |
    // #x20-#xD7FF | #xE000-#xFFFD | #x10000-#x10FFFF. Anything outside it is legitimately refused with
    // InvalidMessageContents, which would be a different check. Covers 1-, 2-, 3- and 4-byte UTF-8
    // sequences plus the three permitted control characters, because an implementation that
    // encrypts/decrypts on a latin-1 or per-character basis corrupts exactly the multi-byte ones.
    private static readonly string[] BodyParts =
    [
        "ascii-plain",
        "\t tab-and-newline \n and-carriage-return \r",
        "2-byte: éüñ Жи",          // Latin-1 supplement, Cyrillic
        "3-byte: 日本語 € �",         // CJK, euro sign, U+FFFD (the top of the range)
        "4-byte: \U0001f600\U0001f9ea\U0010fffd",           // astral plane, up to the #x10FFFF edge
        "boundary: ퟿ ",                          // the two ends of the excluded surrogate gap
    ];

    /// <summary>
    /// With SqsManagedSseEnabled=true a multibyte body and String/Binary attributes round-trip
    /// byte-exact, and MD5OfBody/attribute digests match values recomputed from the plaintext; an
    /// explicit SqsManagedSseEnabled=false queue reads back false and round-trips the same body.
    /// </summary>
    [Test]
    [DisplayName("sqs.sse-sqs-managed-encryption-round-trips-body-byte-exact")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        // SqsManagedSseEnabled and the KMS key id attribute are mutually exclusive, so only the former is set.
        var token = Guid.NewGuid().ToString("N")[..8];
        var body = $"sse-{token}|" + string.Join("|", BodyParts);

        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-"),
            Attributes = new() { ["SqsManagedSseEnabled"] = "true" },
            Tags = Tags(),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var attrs = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["All"] })).Attributes
            ?? throw new KeyNotFoundException("'Attributes'");
        Ensure(attrs.GetValueOrDefault("SqsManagedSseEnabled") == "true",
            "SQS-managed SSE is not enabled on the queue, so nothing below is an encrypted round " +
            $"trip: SqsManagedSseEnabled='{attrs.GetValueOrDefault("SqsManagedSseEnabled")}'");
        Ensure(!attrs.ContainsKey("KmsMasterKeyId"),
            "the queue also carries a CMK, so the encryption under test is ambiguous: " +
            $"KmsMasterKeyId='{attrs.GetValueOrDefault("KmsMasterKeyId")}'");

        // The contrast queue. Real SQS defaults SqsManagedSseEnabled to `true`, so an implementation
        // that hardcodes `true` satisfies the assertion above; only an explicit `false` that reads back
        // as `false` proves the attribute is honoured. It must still round-trip the same body, so a
        // correct implementation cannot pass by refusing the attribute.
        var urlOff = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-") + "-nosse",
            Attributes = new() { ["SqsManagedSseEnabled"] = "false" },
            Tags = Tags(),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(urlOff));
        var offAttrs = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = urlOff, AttributeNames = ["All"] })).Attributes
            ?? throw new KeyNotFoundException("'Attributes'");
        Ensure(offAttrs.GetValueOrDefault("SqsManagedSseEnabled") == "false",
            "a queue created with SqsManagedSseEnabled=false read back " +
            $"'{offAttrs.GetValueOrDefault("SqsManagedSseEnabled")}' -- the attribute is not honoured, so the " +
            "encrypted queue above proves nothing about encryption being enabled");

        var attrString = "attr-" + string.Concat(BodyParts[2..5]);
        var attrBinary = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        Dictionary<string, MessageAttributeValue> MsgAttrs() => new()
        {
            ["s"] = new MessageAttributeValue { DataType = "String", StringValue = attrString },
            ["b"] = new MessageAttributeValue { DataType = "Binary", BinaryValue = new MemoryStream(attrBinary) },
        };

        var sent = await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = body, MessageAttributes = MsgAttrs() });
        var wantBodyMd5 = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(body)));
        // MD5OfMessageBody on the SEND response; MD5OfBody on the RECEIVE one. Not a typo.
        var sentBodyMd5 = sent.MD5OfMessageBody ?? throw new KeyNotFoundException("'MD5OfMessageBody'");
        Ensure(sentBodyMd5 == wantBodyMd5,
            "SendMessage's MD5OfMessageBody must be the digest of the PLAINTEXT body: got " +
            $"'{sentBodyMd5}', computed '{wantBodyMd5}'");
        var sentAttrMd5 = sent.MD5OfMessageAttributes ?? throw new KeyNotFoundException("'MD5OfMessageAttributes'");
        Ensure(sentAttrMd5 == AttrMd5(MsgAttrs()),
            "SendMessage's MD5OfMessageAttributes does not match the digest recomputed from the " +
            $"plaintext attributes: got '{sentAttrMd5}', computed " +
            $"'{AttrMd5(MsgAttrs())}'");

        Message? got = null;
        var deadline = FidelityTime.StartNew();
        while (got is null && deadline.Elapsed < TimeSpan.FromSeconds(60))
        {
#pragma warning disable CS0618 // The check deliberately sends the legacy AttributeNames alongside MessageSystemAttributeNames.
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url,
                MaxNumberOfMessages = 1,
                WaitTimeSeconds = 5,
                MessageAttributeNames = ["All"],
                AttributeNames = ["All"],
                MessageSystemAttributeNames = ["All"],
            });
#pragma warning restore CS0618
            foreach (var m in resp.Messages ?? [])
            {
                got = m;
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle });
            }
        }

        Ensure(got is not null, "the message sent to the SSE-SQS queue was never delivered");

        Ensure(got.Body == body,
            "an SqsManagedSseEnabled queue did not round-trip the body byte-exact: received " +
            $"'{got.Body}', sent '{body}'");
        Ensure(got.MD5OfBody == wantBodyMd5,
            "ReceiveMessage's MD5OfBody must be the digest of the PLAINTEXT body, not of the " +
            $"ciphertext: got '{got.MD5OfBody}', computed '{wantBodyMd5}'");

        var wantAttrMd5 = AttrMd5(MsgAttrs());
        var gotAttrMd5 = got.MD5OfMessageAttributes ?? throw new KeyNotFoundException("'MD5OfMessageAttributes'");
        Ensure(gotAttrMd5 == wantAttrMd5,
            "MD5OfMessageAttributes does not match the digest recomputed from the plaintext " +
            $"attributes: got '{gotAttrMd5}', computed '{wantAttrMd5}'");
        Ensure(got.MessageAttributes!["s"].StringValue == attrString,
            "a String message attribute did not survive the encrypted round trip: " +
            $"'{got.MessageAttributes["s"].StringValue}' != '{attrString}'");
        Ensure(got.MessageAttributes["b"].BinaryValue!.ToArray().AsSpan().SequenceEqual(attrBinary),
            "a Binary message attribute was corrupted by the encrypted round trip: " +
            $"{Convert.ToHexString(got.MessageAttributes["b"].BinaryValue.ToArray())}");

        // The contrast queue carries the same body with SSE off.
        var offSent = await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = urlOff, MessageBody = body });
        Ensure(offSent.MD5OfMessageBody == wantBodyMd5,
            "the unencrypted queue's MD5OfMessageBody differs from the encrypted queue's for the " +
            "identical body -- the digest must not depend on encryption at all: got " +
            $"'{offSent.MD5OfMessageBody}', computed '{wantBodyMd5}'");
        Message? offGot = null;
        var offDeadline = FidelityTime.StartNew();
        while (offGot is null && offDeadline.Elapsed < TimeSpan.FromSeconds(60))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = urlOff, MaxNumberOfMessages = 1, WaitTimeSeconds = 5 });
            foreach (var m in resp.Messages ?? [])
            {
                offGot = m;
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = urlOff, ReceiptHandle = m.ReceiptHandle });
            }
        }

        Ensure(offGot is not null, "the message sent to the SSE-disabled contrast queue was never delivered");
        Ensure(offGot.Body == body,
            "the SSE-disabled contrast queue did not round-trip the body byte-exact either, so the " +
            "byte-exactness above is not attributable to the encryption path: received " +
            $"'{offGot.Body}'");
    }

    /// <summary>
    /// The documented SQS message-attribute digest, computed independently of botocore.
    ///
    /// Per attribute in name order: length-prefixed name, data type, a transport byte, then the value.
    /// </summary>
    private static string AttrMd5(Dictionary<string, MessageAttributeValue> attrs)
    {
        var buf = new List<byte>();
        void AppendLengthPrefixed(byte[] raw)
        {
            var len = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(len, (uint)raw.Length);
            buf.AddRange(len);
            buf.AddRange(raw);
        }

        foreach (var name in attrs.Keys.Order(StringComparer.Ordinal))
        {
            var spec = attrs[name];
            foreach (var text in new[] { name, spec.DataType })
            {
                AppendLengthPrefixed(Encoding.UTF8.GetBytes(text));
            }

            byte[] raw;
            if (spec.StringValue is not null)
            {
                raw = Encoding.UTF8.GetBytes(spec.StringValue);
                buf.Add(0x01);
            }
            else
            {
                raw = spec.BinaryValue.ToArray();
                buf.Add(0x02);
            }

            AppendLengthPrefixed(raw);
        }

        return Convert.ToHexStringLower(MD5.HashData(buf.ToArray()));
    }
}

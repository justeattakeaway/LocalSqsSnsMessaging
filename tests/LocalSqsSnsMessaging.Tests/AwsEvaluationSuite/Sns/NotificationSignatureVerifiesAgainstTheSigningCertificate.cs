// Translated from behavioral/sns/check_notification_signature_verifies_against_the_signing_certificate.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.notification-signature-verifies-against-the-signing-certificate</summary>
[Category("AwsEvaluationSuite")]
public sealed class NotificationSignatureVerifiesAgainstTheSigningCertificate
{
    private const string Scenario = "sns-signature";

    // DigestInfo DER prefixes for RSASSA-PKCS1-v1_5, keyed by the SNS SignatureVersion that selects
    // the digest: version 1 signs with SHA-1, version 2 with SHA-256.
    private static readonly Dictionary<string, (Func<byte[], byte[]> Hasher, byte[] Prefix)> Digests = new()
    {
#pragma warning disable CA5350 // SNS SignatureVersion 1 is defined as SHA-1.
        ["1"] = (SHA1.HashData, Convert.FromHexString("3021300906052b0e03021a05000414")),
#pragma warning restore CA5350
        ["2"] = (SHA256.HashData, Convert.FromHexString("3031300d060960864801650304020105000420")),
    };

    /// <summary>
    /// A delivered notification's Signature verifies with the SigningCertURL certificate's public
    /// key over SNS's canonical string, and one flipped character of Message makes it fail, so the
    /// verifier is shown to check something.
    /// </summary>
    [Test]
    [DisplayName("sns.notification-signature-verifies-against-the-signing-certificate")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsEnvelope, "the envelope's SigningCertURL and Signature are the placeholder 'EXAMPLE', so the signature can't be verified")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = await CreateTopic(ctx);
        var (queueUrl, queueArn) = await CreateQueueForTopic(ctx, topicArn);
        await SubscribeAndConfirm(ctx, topicArn, queueArn);

        var declared = (await sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = topicArn }))
            .Attributes?.GetValueOrDefault("SignatureVersion", "1") ?? "1";
        var payload = FidelityContext.UniqueName("sim2real-behav-sns-signed-");
        const string subject = "sim2real-signed-subject";
        await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Subject = subject, Message = payload });

        var envelope = JsonNode.Parse(await AwaitBody(ctx, queueUrl, payload))!.AsObject();
        Ensure(Text(envelope["SignatureVersion"]) == declared,
            $"the delivered SignatureVersion '{envelope["SignatureVersion"]}' does not match the " +
            $"topic's declared SignatureVersion '{declared}'");
        var version = Text(envelope["SignatureVersion"])!;
        Ensure(Digests.ContainsKey(version), $"unexpected SignatureVersion: '{version}'");

        var certUrl = Text(envelope["SigningCertURL"]);
        var p = FidelityContext.PublishedUrlMismatch(certUrl);
        Ensure(p is null, p ?? "");

        string pem;
        using (var resp = await ctx.GetPublishedUrl(certUrl!, cancellationToken))
        {
            resp.EnsureSuccessStatusCode();
            pem = Encoding.ASCII.GetString(await resp.Content.ReadAsByteArrayAsync(cancellationToken));
        }

        Ensure(pem.Contains("BEGIN CERTIFICATE", StringComparison.Ordinal), $"SigningCertURL did not serve a PEM certificate: '{pem[..Math.Min(120, pem.Length)]}'");
        var (modulus, exponent) = RsaPublicKeyFromCertificate(pem);

        var signature = Convert.FromBase64String(Text(envelope["Signature"])!);
        var canonical = CanonicalString(envelope);
        var verified = VerifyPkcs1V15(Encoding.UTF8.GetBytes(canonical), signature, modulus, exponent, version);
        Ensure(verified,
            $"the notification signature does not verify against the certificate at '{certUrl}'; " +
            $"canonical string was '{canonical}'");

        var tampered = envelope.DeepClone().AsObject();
        var message = Text(envelope["Message"])!;
        tampered["Message"] = message[..^1] + (message[^1] != 'X' ? "X" : "Y");
        var tamperedCanonical = CanonicalString(tampered);
        Ensure(tamperedCanonical != canonical,
            "the tamper did not change the canonical string, so the negative half proves nothing: " +
            $"'{tamperedCanonical}'");
        var tamperedVerified = VerifyPkcs1V15(Encoding.UTF8.GetBytes(tamperedCanonical), signature, modulus, exponent, version);
        Ensure(!tamperedVerified,
            "a tampered canonical string still verified, so the verification is not actually " +
            $"checking anything (modulus bits {modulus.GetBitLength()}, exponent {exponent})");
        Ensure(Text(envelope["Subject"]) == subject,
            $"the Subject that the canonical string signs was not delivered: '{Text(envelope["Subject"])}'");
    }

    /// <summary><c>str(value)</c> of a parsed JSON value, or <see langword="null"/> for a missing one.</summary>
    private static string? Text(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : node?.ToJsonString();

    /// <summary>
    /// The string SNS signs for a Notification: field\nvalue\n for each signed key, in the order
    /// SNS documents (Message, MessageId, Subject when present, Timestamp, TopicArn, Type).
    /// </summary>
    private static string CanonicalString(JsonObject envelope)
    {
        string[] order = ["Message", "MessageId", "Subject", "Timestamp", "TopicArn", "Type"];
        var parts = new StringBuilder();
        foreach (var key in order)
        {
            if (key == "Subject" && !envelope.ContainsKey("Subject"))
            {
                continue;
            }

            if (!envelope.ContainsKey(key))
            {
                throw new KeyNotFoundException($"'{key}'");
            }

            parts.Append(key).Append('\n').Append(Text(envelope[key]) ?? "None").Append('\n');
        }

        return parts.ToString();
    }

    /// <summary>Read one DER tag-length-value at offset i; returns (tag, content, next_offset).</summary>
    private static (int Tag, byte[] Content, int Next) DerTlv(byte[] buf, int i)
    {
        int tag = buf[i];
        i += 1;
        int length = buf[i];
        i += 1;
        if ((length & 0x80) != 0)
        {
            var n = length & 0x7F;
            if (i + n > buf.Length)
            {
                throw new ArgumentException("truncated DER");
            }

            length = (int)new BigInteger(buf.AsSpan(i, n), isUnsigned: true, isBigEndian: true);
            i += n;
        }

        var end = i + length;
        if (end > buf.Length || end < i)
        {
            throw new ArgumentException("truncated DER");
        }

        return (tag, buf[i..end], end);
    }

    /// <summary>
    /// Extract (modulus, exponent) from an X.509 PEM certificate with no external crypto library.
    /// Walks the DER for the SubjectPublicKeyInfo BIT STRING whose contents parse as
    /// RSAPublicKey ::= SEQUENCE { modulus INTEGER, publicExponent INTEGER }. The size and exponent
    /// constraints reject a BIT STRING that merely happens to look like one; a wrong key would in any
    /// case make the signature verification below fail loudly rather than pass.
    /// </summary>
    private static (BigInteger Modulus, BigInteger Exponent) RsaPublicKeyFromCertificate(string pem)
    {
        var body = string.Concat(pem.Split('\n').Select(l => l.TrimEnd('\r')).Where(line => line.Length > 0 && !line.StartsWith("-----", StringComparison.Ordinal)));
        var der = Convert.FromBase64String(body);
        var stack = new List<byte[]> { der };
        while (stack.Count > 0)
        {
            var buf = stack[0];
            stack.RemoveAt(0);
            var i = 0;
            while (i < buf.Length)
            {
                int tag;
                byte[] content;
                try
                {
                    (tag, content, i) = DerTlv(buf, i);
                }
                catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException)
                {
                    break;
                }

                if (tag == 0x03 && content.Length > 0 && content[0] == 0x00)
                {
                    try
                    {
                        var (innerTag, inner, _) = DerTlv(content[1..], 0);
                        if (innerTag == 0x30)
                        {
                            var (tN, cN, j) = DerTlv(inner, 0);
                            var (tE, cE, _) = DerTlv(inner, j);
                            if (tN == 0x02 && tE == 0x02)
                            {
                                var n = new BigInteger(cN, isUnsigned: true, isBigEndian: true);
                                var e = new BigInteger(cE, isUnsigned: true, isBigEndian: true);
                                if (n.GetBitLength() is >= 1024 and <= 8192 && e >= 3 && e <= BigInteger.One << 32)
                                {
                                    return (n, e);
                                }
                            }
                        }
                    }
                    catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException)
                    {
                    }
                }

                if ((tag & 0x20) != 0)
                {
                    stack.Add(content);
                }
            }
        }

        throw Fail($"no RSA public key found in the certificate served by SigningCertURL ({der.Length} DER bytes)");
    }

    /// <summary>RSASSA-PKCS1-v1_5 verify, with the digest SNS's SignatureVersion selects.</summary>
    private static bool VerifyPkcs1V15(byte[] signed, byte[] signature, BigInteger modulus, BigInteger exponent, string version)
    {
        var (hasher, prefix) = Digests[version];
        var k = (int)((modulus.GetBitLength() + 7) / 8);
        if (signature.Length != k)
        {
            return false;
        }

        var m = BigInteger.ModPow(new BigInteger(signature, isUnsigned: true, isBigEndian: true), exponent, modulus)
            .ToByteArray(isUnsigned: true, isBigEndian: true);
        var em = new byte[k];
        m.CopyTo(em, k - m.Length);
        var digest = hasher(signed);
        var psLen = k - prefix.Length - digest.Length - 3;
        if (psLen < 8)
        {
            return false;
        }

        byte[] expected = [0x00, 0x01, .. Enumerable.Repeat((byte)0xff, psLen), 0x00, .. prefix, .. digest];
        return em.AsSpan().SequenceEqual(expected);
    }

    private static List<Amazon.SimpleNotificationService.Model.Tag> TagsKv() =>
        [new() { Key = "sim2real", Value = Scenario }, new() { Key = "sim2real:managed", Value = "true" }];

    /// <summary>CreateTopic (tagged + prefix-named) and register DeleteTopic immediately; returns the ARN.</summary>
    private static async Task<string> CreateTopic(FidelityContext ctx, bool fifo = false, Dictionary<string, string>? attributes = null)
    {
        var name = FidelityContext.UniqueName("sim2real-behav-sns-t-");
        var attrs = new Dictionary<string, string>(attributes ?? []);
        if (fifo)
        {
            name += ".fifo";
            attrs["FifoTopic"] = "true";
        }

        var arn = (await ctx.Sns.CreateTopicAsync(new CreateTopicRequest { Name = name, Attributes = attrs, Tags = TagsKv() })).TopicArn;
        ctx.OnCleanup(() => ctx.Sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>Create a real SQS queue whose policy lets exactly this topic publish to it.</summary>
    private static async Task<(string Url, string Arn)> CreateQueueForTopic(FidelityContext ctx, string topicArn, bool fifo = false)
    {
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-q-");
        var attrs = new Dictionary<string, string>();
        if (fifo)
        {
            name += ".fifo";
            attrs["FifoQueue"] = "true";
        }

        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = name,
            Attributes = attrs,
            Tags = new() { ["sim2real"] = Scenario, ["sim2real:managed"] = "true" },
        })).QueueUrl;
        ctx.OnCleanup(() => ctx.Sqs.DeleteQueueAsync(url));
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];
        var policy = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Effect"] = "Allow",
                ["Principal"] = new JsonObject { ["Service"] = "sns.amazonaws.com" },
                ["Action"] = "sqs:SendMessage",
                ["Resource"] = arn,
                ["Condition"] = new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = topicArn } },
            }),
        };
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = url, Attributes = new() { ["Policy"] = policy.ToJsonString() } });
        return (url, arn);
    }

    /// <summary>Subscribe an SQS queue and poll GetSubscriptionAttributes until it is confirmed.</summary>
    private static async Task<string> SubscribeAndConfirm(FidelityContext ctx, string topicArn, string queueArn,
        Dictionary<string, string>? attributes = null, double timeoutS = 60.0)
    {
        var sns = ctx.Sns;
        var request = new SubscribeRequest { TopicArn = topicArn, Protocol = "sqs", Endpoint = queueArn, ReturnSubscriptionArn = true };
        if (attributes is { Count: > 0 })
        {
            request.Attributes = attributes;
        }

        var subArn = (await sns.SubscribeAsync(request)).SubscriptionArn;
        ctx.OnCleanup(() => ctx.Sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subArn }));
        var deadline = FidelityTime.StartNew();
        string? pending = null;
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            pending = (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn }))
                .Attributes?.GetValueOrDefault("PendingConfirmation");
            if (pending == "false")
            {
                return subArn;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1.0));
        }

        throw Fail($"subscription '{subArn}' was still PendingConfirmation='{pending}' after {timeoutS}s");
    }

    /// <summary>Long-poll the queue until a body containing needle arrives; returns that body.</summary>
    private static async Task<string> AwaitBody(FidelityContext ctx, string queueUrl, string needle, double timeoutS = 90.0)
    {
        var sqs = ctx.Sqs;
        var seen = new List<string>();
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                var body = msg.Body ?? "";
                seen.Add(body);
                if (body.Contains(needle, StringComparison.Ordinal))
                {
                    return body;
                }
            }
        }

        throw Fail($"no message containing '{needle}' was delivered within {timeoutS}s; " +
            $"bodies seen: [{string.Join(", ", seen.Select(b => $"'{b[..Math.Min(120, b.Length)]}'"))}]");
    }
}

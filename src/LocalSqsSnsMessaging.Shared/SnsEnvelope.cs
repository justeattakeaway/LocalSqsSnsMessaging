using LocalSqsSnsMessaging.Sns.Model;

namespace LocalSqsSnsMessaging;

/// <summary>
/// Writes the JSON documents SNS delivers (a <c>Notification</c>, a <c>SubscriptionConfirmation</c>)
/// byte for byte as SNS lays them out, signed as SNS signs them.
/// See https://docs.aws.amazon.com/sns/latest/dg/sns-message-and-json-formats.html
/// </summary>
/// <remarks>
/// SNS pretty-prints: one <c>"Key" : value</c> line per member, indented two spaces, in a fixed
/// member order, with each message attribute on one compact line. Strings are written with only
/// the escapes JSON requires, so non-ASCII text arrives as UTF-8 rather than <c>\u</c> escapes.
/// </remarks>
internal static class SnsEnvelope
{
    /// <summary>The <c>SignatureVersion</c> of a topic that hasn't set one: SHA1withRSA.</summary>
    public const string DefaultSignatureVersion = "1";

    /// <summary>The members SNS signs, when present: a FIFO topic's <c>SequenceNumber</c> is not one of them.</summary>
    private static readonly HashSet<string> SignedKeys = new(StringComparer.Ordinal)
    {
        "Message", "MessageId", "Subject", "SubscribeURL", "Timestamp", "Token", "TopicArn", "Type",
    };

    /// <summary>The <c>Signature</c> and <c>SigningCertURL</c> where no signing certificate can be built (netstandard2.0).</summary>
    private const string Placeholder = "EXAMPLE";

    /// <summary>
    /// A <c>Notification</c>: Type, MessageId, SequenceNumber (from a FIFO topic), TopicArn, Subject
    /// (when given), Message, Timestamp, SignatureVersion, Signature, SigningCertURL, UnsubscribeURL,
    /// then MessageAttributes (when any).
    /// </summary>
    public static string Notification(
        InMemoryAwsBus bus,
        string signatureVersion,
        string messageId,
        string? sequenceNumber,
        string topicArn,
        string? subject,
        string message,
        string unsubscribeUrl,
        Dictionary<string, MessageAttributeValue>? attributes,
        SnsNotificationStamp stamp)
    {
        var members = new List<KeyValuePair<string, string>>(11)
        {
            new("Type", "Notification"),
            new("MessageId", messageId),
        };
        if (sequenceNumber is not null)
        {
            members.Add(new("SequenceNumber", sequenceNumber));
        }

        members.Add(new("TopicArn", topicArn));
        if (subject is not null)
        {
            members.Add(new("Subject", subject));
        }

        members.Add(new("Message", message));
        members.Add(new("Timestamp", stamp.Timestamp(bus)));
        AddSignature(members, signatureVersion, stamp.Signature(signatureVersion, message, () => Sign(bus, members, signatureVersion)));
        members.Add(new("UnsubscribeURL", unsubscribeUrl));

        return Write(members, attributes);
    }

    /// <summary>
    /// A <c>SubscriptionConfirmation</c>: Type, MessageId, Token, TopicArn, Message, SubscribeURL,
    /// Timestamp, SignatureVersion, Signature, SigningCertURL.
    /// </summary>
    public static string SubscriptionConfirmation(
        InMemoryAwsBus bus,
        string signatureVersion,
        string messageId,
        string token,
        string topicArn,
        string message,
        string subscribeUrl)
    {
        var members = new List<KeyValuePair<string, string>>(10)
        {
            new("Type", "SubscriptionConfirmation"),
            new("MessageId", messageId),
            new("Token", token),
            new("TopicArn", topicArn),
            new("Message", message),
            new("SubscribeURL", subscribeUrl),
            new("Timestamp", SnsHttpDelivery.Timestamp(bus)),
        };
        AddSignature(members, signatureVersion, Sign(bus, members, signatureVersion));

        return Write(members, attributes: null);
    }

    /// <summary>
    /// The string SNS signs: <c>Key\nValue\n</c> for each of <paramref name="members"/>, in key order.
    /// </summary>
    private static string CanonicalString(IEnumerable<KeyValuePair<string, string>> members)
    {
        var canonical = new StringBuilder();
        foreach (var (key, value) in members.Where(member => SignedKeys.Contains(member.Key)).OrderBy(member => member.Key, StringComparer.Ordinal))
        {
            canonical.Append(key).Append('\n').Append(value).Append('\n');
        }

        return canonical.ToString();
    }

    /// <summary>
    /// The Signature and SigningCertURL for the members already present: for a notification Message,
    /// MessageId, Subject, Timestamp, TopicArn and Type (its UnsubscribeURL comes after, unsigned), for
    /// a confirmation Message, MessageId, SubscribeURL, Timestamp, Token, TopicArn and Type.
    /// </summary>
    private static (string Signature, string CertificateUrl) Sign(
        InMemoryAwsBus bus, List<KeyValuePair<string, string>> members, string signatureVersion)
    {
        var certificate = SnsSigningCertificate.Current;
        if (certificate is null)
        {
            return (Placeholder, Placeholder);
        }

        return (certificate.Sign(CanonicalString(members), signatureVersion) ?? Placeholder,
            SnsHttpDelivery.SigningCertUrl(bus, certificate));
    }

    /// <summary>Appends SignatureVersion, Signature and SigningCertURL.</summary>
    private static void AddSignature(
        List<KeyValuePair<string, string>> members, string signatureVersion, (string Signature, string CertificateUrl) signed)
    {
        members.Add(new("SignatureVersion", signatureVersion));
        members.Add(new("Signature", signed.Signature));
        members.Add(new("SigningCertURL", signed.CertificateUrl));
    }

    private static string Write(List<KeyValuePair<string, string>> members, Dictionary<string, MessageAttributeValue>? attributes)
    {
        var json = new StringBuilder("{\n");
        for (var i = 0; i < members.Count; i++)
        {
            if (i > 0)
            {
                json.Append(",\n");
            }

            json.Append("  ");
            AppendString(json, members[i].Key);
            json.Append(" : ");
            AppendString(json, members[i].Value);
        }

        if (attributes is { Count: > 0 })
        {
            json.Append(",\n  \"MessageAttributes\" : {\n");
            var first = true;
            foreach (var (name, value) in attributes)
            {
                if (!first)
                {
                    json.Append(",\n");
                }

                first = false;
                json.Append("    ");
                AppendString(json, name);
                json.Append(" : {\"Type\":");
                AppendString(json, value.DataType ?? string.Empty);
                json.Append(",\"Value\":");
                AppendString(json, value.StringValue ?? Convert.ToBase64String(value.BinaryValue?.ToArray() ?? []));
                json.Append('}');
            }

            json.Append("\n  }");
        }

        return json.Append("\n}").ToString();
    }

    /// <summary>A JSON string literal, escaping only the quote, the backslash and control characters.</summary>
    private static void AppendString(StringBuilder json, string value)
    {
        json.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    json.Append("\\\"");
                    break;
                case '\\':
                    json.Append("\\\\");
                    break;
                case '\b':
                    json.Append("\\b");
                    break;
                case '\f':
                    json.Append("\\f");
                    break;
                case '\n':
                    json.Append("\\n");
                    break;
                case '\r':
                    json.Append("\\r");
                    break;
                case '\t':
                    json.Append("\\t");
                    break;
                case < ' ':
                    json.Append("\\u00").Append(((int)c).ToString("X2", NumberFormatInfo.InvariantInfo));
                    break;
                default:
                    json.Append(c);
                    break;
            }
        }

        json.Append('"');
    }
}

/// <summary>
/// The Timestamp and signatures of one published message. Every subscription's envelope signs the
/// same members (the UnsubscribeURL, the only one that differs, isn't signed), so a message is
/// stamped once, and signed once per signature version and body, however many subscriptions get it.
/// </summary>
internal sealed class SnsNotificationStamp
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Version, string Body), (string Signature, string CertificateUrl)> _signatures = [];
    private string? _timestamp;

    /// <summary>The message's Timestamp: when it was first written into an envelope.</summary>
    public string Timestamp(InMemoryAwsBus bus)
    {
        lock (_gate)
        {
            return _timestamp ??= SnsHttpDelivery.Timestamp(bus);
        }
    }

    /// <summary>
    /// The signature of the message with this <paramref name="body"/> (a <c>MessageStructure=json</c>
    /// message has one per protocol) under <paramref name="signatureVersion"/>, made by
    /// <paramref name="sign"/> the first time it is needed.
    /// </summary>
    public (string Signature, string CertificateUrl) Signature(
        string signatureVersion, string body, Func<(string Signature, string CertificateUrl)> sign)
    {
        lock (_gate)
        {
            if (!_signatures.TryGetValue((signatureVersion, body), out var signed))
            {
                signed = sign();
                _signatures[(signatureVersion, body)] = signed;
            }

            return signed;
        }
    }
}

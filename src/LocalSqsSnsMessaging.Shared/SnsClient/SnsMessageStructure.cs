using System.Text.Json;

namespace LocalSqsSnsMessaging;

/// <summary>
/// The bodies a published message delivers. With <c>MessageStructure=json</c> the message is a JSON
/// object holding a <c>default</c> body and optional per-protocol bodies (<c>"sqs"</c>,
/// <c>"https"</c>, ...), and each subscription receives the body for its protocol, else the default.
/// Any other <c>MessageStructure</c> delivers the message as sent to every subscription.
/// </summary>
internal sealed class SnsMessageStructure
{
    private const string Json = "json";
    private const string DefaultKey = "default";

    private readonly string _defaultBody;
    private readonly Dictionary<string, string>? _protocolBodies;

    private SnsMessageStructure(string defaultBody, Dictionary<string, string>? protocolBodies)
    {
        _defaultBody = defaultBody;
        _protocolBodies = protocolBodies;
    }

    /// <summary>
    /// Reads <paramref name="message"/> as <paramref name="messageStructure"/> says to, refusing a
    /// <c>json</c> message as SNS does when it isn't a JSON object or has no scalar <c>default</c>.
    /// </summary>
    /// <remarks>
    /// A string branch is delivered as its text and a number, boolean or <c>null</c> as its JSON
    /// text. A protocol branch that is <c>null</c>, an object or an array is ignored, so that protocol
    /// falls back to <c>default</c>; a <c>default</c> that is an object or array counts as missing.
    /// </remarks>
    public static SnsMessageStructure Parse(string message, string? messageStructure)
    {
        if (!string.Equals(messageStructure, Json, StringComparison.Ordinal))
        {
            return new SnsMessageStructure(message, null);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(message);
        }
        catch (JsonException)
        {
            throw FailedToParse();
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw FailedToParse();
            }

            string? defaultBody = null;
            var protocolBodies = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var branch in document.RootElement.EnumerateObject())
            {
                if (string.Equals(branch.Name, DefaultKey, StringComparison.Ordinal))
                {
                    defaultBody = branch.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                        ? null
                        : TextOf(branch.Value);
                }
                else if (branch.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Object or JsonValueKind.Array))
                {
                    protocolBodies[branch.Name] = TextOf(branch.Value);
                }
            }

            return defaultBody is null
                ? throw new InternalInvalidParameterException(
                    "Invalid parameter: Message Structure - No default entry in JSON message body")
                : new SnsMessageStructure(defaultBody, protocolBodies);
        }
    }

    /// <summary>The body a subscription with <paramref name="protocol"/> receives.</summary>
    public string BodyFor(string protocol) =>
        _protocolBodies is not null && _protocolBodies.TryGetValue(protocol, out var body) ? body : _defaultBody;

    private static string TextOf(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();

    private static InternalInvalidParameterException FailedToParse() =>
        new("Invalid parameter: Message Structure - JSON message body failed to parse");
}

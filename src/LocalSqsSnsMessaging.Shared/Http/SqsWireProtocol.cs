using System.Net;
using System.Text;

namespace LocalSqsSnsMessaging.Http;

/// <summary>
/// How an HTTP request reaches SQS, and how SQS refuses one that never names an operation.
/// Shared by <c>InMemoryAwsHttpMessageHandler</c> and the server's <c>AwsBridgeMiddleware</c>, so
/// the two answer the same raw request the same way.
/// </summary>
/// <remarks>
/// SQS serves two wire protocols over one queue state: JSON (<c>application/x-amz-json-1.0</c>
/// with an <c>X-Amz-Target: AmazonSQS.&lt;Operation&gt;</c> header), which current SDKs use, and
/// the AWS Query protocol it was born with (form-encoded <c>Action=...&amp;Version=2012-11-05</c>
/// parameters, XML responses). The Content-Type picks the protocol.
/// </remarks>
internal static class SqsWireProtocol
{
    /// <summary>The Query protocol's API version for SQS (SNS's is 2010-03-31).</summary>
    public const string QueryApiVersion = "2012-11-05";

    /// <summary>The JSON protocol's Content-Type; SQS refuses any other with a JSON target.</summary>
    public const string JsonContentType = "application/x-amz-json-1.0";

    /// <summary>The Query protocol's Content-Type.</summary>
    public const string FormContentType = "application/x-www-form-urlencoded";

    /// <summary>
    /// The largest request SQS's front end accepts, in bytes of content once the wire encoding is
    /// undone (see <see cref="IsTooLarge"/>); a bigger one is refused with HTTP 413 before SQS looks
    /// at a single field. A message may be at most 1 MiB, so this leaves room for a maximum-size
    /// message and the rest of its request, while a 2 MiB message is refused.
    /// </summary>
    public const long MaxRequestBodyBytes = 2L * 1024 * 1024;

    /// <summary>The body of SQS's refusal of a JSON-typed request without a known X-Amz-Target (HTTP 400).</summary>
    public const string UnknownOperationJson = """{"__type":"com.amazon.coral.service#UnknownOperationException"}""";

    /// <summary>The body of SQS's refusal of a request whose Content-Type names neither protocol (HTTP 404).</summary>
    public const string UnknownOperationXml = "<UnknownOperationException/>\n";

    /// <summary>The body of SQS's front end's refusal of an oversized request (HTTP 413).</summary>
    public const string RequestEntityTooLargeBody = "<html><body><b>Http/1.1 Request Entity Too Large</b></body></html>\n";

    /// <summary>
    /// True if a request body is over <see cref="MaxRequestBodyBytes"/>. Its size is measured once the
    /// wire encoding is undone (JSON string escapes, form percent-encoding): SDKs escape freely (the
    /// AWS SDK for .NET writes <c>&lt;</c> and every non-ASCII character as a six-byte <c>\uXXXX</c>),
    /// so a valid 1 MiB message can take several MiB on the wire. A body within the limit as sent is
    /// never decoded here.
    /// </summary>
    public static bool IsTooLarge(ReadOnlySpan<byte> body, bool isForm)
    {
        if (body.Length <= MaxRequestBodyBytes)
        {
            return false;
        }

        return (isForm ? DecodedFormSize(body) : DecodedJsonSize(body)) > MaxRequestBodyBytes;
    }

    private static long DecodedFormSize(ReadOnlySpan<byte> body)
    {
        long size = 0;
        foreach (var parameter in QueryStringParser.Parse(body))
        {
            size += Encoding.UTF8.GetByteCount(parameter.Key) + 1 + Encoding.UTF8.GetByteCount(parameter.Value);
        }

        return size;
    }

    private static long DecodedJsonSize(ReadOnlySpan<byte> body)
    {
        long size = 0;
        var reader = new System.Text.Json.Utf8JsonReader(body);
        try
        {
            while (reader.Read())
            {
                size += reader.TokenType is System.Text.Json.JsonTokenType.String or System.Text.Json.JsonTokenType.PropertyName
                    ? Encoding.UTF8.GetByteCount(reader.GetString()!)
                    : reader.HasValueSequence ? reader.ValueSequence.Length : reader.ValueSpan.Length;
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Not JSON at all: nothing to undo.
            return body.Length;
        }

        return size;
    }

    /// <summary>True if <paramref name="mediaType"/> (without parameters) is the Query protocol's.</summary>
    public static bool IsForm(string? mediaType) => string.Equals(mediaType, FormContentType, StringComparison.OrdinalIgnoreCase);

    /// <summary>True if <paramref name="mediaType"/> (without parameters) is the JSON protocol's.</summary>
    public static bool IsJson(string? mediaType) => string.Equals(mediaType, JsonContentType, StringComparison.OrdinalIgnoreCase);

    /// <summary>The operation of an <c>X-Amz-Target</c> value (<c>AmazonSQS.SendMessage</c>), or null.</summary>
    public static string? OperationFromTarget(string? target)
    {
        if (string.IsNullOrEmpty(target))
        {
            return null;
        }

        var parts = target!.Split('.');
        return parts.Length == 2 && parts[1].Length > 0 ? parts[1] : null;
    }

    /// <summary>
    /// True if form-encoded Query parameters are addressed to SQS rather than SNS, which shares the
    /// server's endpoint: by their API <c>Version</c>, or else by the SigV4 credential scope.
    /// </summary>
    public static bool IsSqsQueryRequest(Dictionary<string, string> parameters, string? authorizationHeader)
    {
        if (parameters.TryGetValue("Version", out var version))
        {
            return string.Equals(version, QueryApiVersion, StringComparison.Ordinal);
        }

        // Credential=AKID/20240101/us-east-1/sqs/aws4_request
        return authorizationHeader is not null
            && authorizationHeader.Contains("/sqs/aws4_request", StringComparison.Ordinal);
    }

    public static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>The status code of each refusal.</summary>
    public const HttpStatusCode UnknownOperationJsonStatus = HttpStatusCode.BadRequest;

    public const HttpStatusCode UnknownOperationXmlStatus = HttpStatusCode.NotFound;

    public const HttpStatusCode RequestEntityTooLargeStatus = HttpStatusCode.RequestEntityTooLarge;
}

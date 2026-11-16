using System.Net;
using System.Security.Cryptography;

namespace LocalSqsSnsMessaging.Http;

/// <summary>
/// How a wire protocol answers one request, apart from the body it wrote into the host's buffer:
/// the status and the headers. The library and the server each copy it onto their own response
/// type, so neither knows any protocol's headers.
/// </summary>
internal readonly struct WireResponse
{
    private static readonly RandomNumberGenerator Random = RandomNumberGenerator.Create();

    public WireResponse(HttpStatusCode status, string contentType, string requestId, string? queryError = null, string? crc32 = null)
    {
        Status = status;
        ContentType = contentType;
        RequestId = requestId;
        QueryError = queryError;
        Crc32 = crc32;
    }

    public HttpStatusCode Status { get; }

    /// <summary>The <c>Content-Type</c> of the body, with its parameters.</summary>
    public string ContentType { get; }

    /// <summary>The <c>x-amzn-RequestId</c> header, and the request id of any error document.</summary>
    public string RequestId { get; }

    /// <summary>
    /// The <c>x-amzn-query-error</c> header SQS sends with a JSON error (SQS is
    /// <c>awsQueryCompatible</c>: SDKs read the Query protocol's error code from it); null otherwise.
    /// </summary>
    public string? QueryError { get; }

    /// <summary>The <c>x-amz-crc32</c> header DynamoDB sends with every response; null for other services.</summary>
    public string? Crc32 { get; }

    /// <summary>A new request id, as AWS writes one.</summary>
    public static string NewRequestId() => Guid.NewGuid().ToString();

    /// <summary>A new DynamoDB request id: 52 upper-case letters and digits, as DynamoDB writes one.</summary>
    public static string NewDynamoDbRequestId()
    {
        const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        const int Length = 52;
        var bytes = new byte[Length];
        Random.GetBytes(bytes);

        var chars = new char[Length];
        for (var i = 0; i < Length; i++)
        {
            chars[i] = Alphabet[bytes[i] % Alphabet.Length];
        }

        return new string(chars);
    }
}

using System.Net;

namespace LocalSqsSnsMessaging.Http;

/// <summary>
/// How a wire protocol answers one request, apart from the body it wrote into the host's buffer:
/// the status and the headers. The library and the server each copy it onto their own response
/// type, so neither knows any protocol's headers.
/// </summary>
internal readonly struct WireResponse
{
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
}

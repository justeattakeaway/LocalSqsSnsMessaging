using System.Net;

namespace LocalSqsSnsMessaging;

/// <summary>
/// Base class for all AWS service responses.
/// Mirrors Amazon.Runtime.AmazonWebServiceResponse.
/// </summary>
internal class AmazonWebServiceResponse
{
    public HttpStatusCode HttpStatusCode { get; set; }
    public ResponseMetadata ResponseMetadata { get; set; } = new();
    public long ContentLength { get; set; }
}

/// <summary>
/// Contains metadata about an AWS service response.
/// Mirrors Amazon.Runtime.ResponseMetadata.
/// </summary>
internal class ResponseMetadata
{
    public string RequestId { get; set; } = string.Empty;
}

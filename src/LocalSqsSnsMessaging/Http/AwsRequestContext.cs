namespace LocalSqsSnsMessaging.Http;

/// <summary>
/// Encapsulates the context of an AWS service request including service type, operation, and request data.
/// </summary>
internal sealed class AwsRequestContext
{
    /// <summary>
    /// Gets or sets the AWS service type (SQS or SNS).
    /// </summary>
    public required AwsServiceType ServiceType { get; init; }

    /// <summary>
    /// Gets or sets the operation name (e.g., "SendMessage", "CreateQueue").
    /// </summary>
    public required string OperationName { get; init; }

    /// <summary>
    /// Gets or sets the request body content.
    /// </summary>
    public required string RequestBody { get; init; }

    /// <summary>
    /// Gets or sets the request headers.
    /// </summary>
    public required Dictionary<string, string> Headers { get; init; }
}

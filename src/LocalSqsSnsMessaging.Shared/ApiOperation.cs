namespace LocalSqsSnsMessaging;

/// <summary>
/// Represents a single API operation that was executed against the in-memory AWS bus.
/// Used for tracking API usage to help identify required IAM policies.
/// </summary>
public sealed record ApiOperation
{
    /// <summary>
    /// Gets the AWS service name (e.g., "sqs" or "sns").
    /// </summary>
    public required string Service { get; init; }

    /// <summary>
    /// Gets the API action name (e.g., "SendMessage", "Publish").
    /// </summary>
    public required string Action { get; init; }

    /// <summary>
    /// Gets the ARN of the resource accessed, if applicable.
    /// May be null for operations that don't target a specific resource (e.g., ListQueues).
    /// </summary>
    public string? ResourceArn { get; init; }

    /// <summary>
    /// Gets the timestamp when the operation was executed.
    /// </summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Gets whether the operation completed successfully.
    /// </summary>
    public bool Success { get; init; }
}

using System.Collections.Concurrent;

namespace LocalSqsSnsMessaging;

/// <summary>
/// Represents an in-memory implementation of AWS messaging services (SQS and SNS).
/// </summary>
public sealed class InMemoryAwsBus
{
    /// <summary>
    /// Gets or initializes the TimeProvider used for time-related operations.
    /// </summary>
    /// <value>The TimeProvider instance, defaulting to TimeProvider.System.</value>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>
    /// Gets or initializes the current AWS account ID.
    /// </summary>
    /// <value>The AWS account ID as a string, defaulting to "000000000000".</value>
    public string CurrentAccountId { get; init; } = "000000000000";

    /// <summary>
    /// Gets or initializes the current AWS region.
    /// </summary>
    /// <value>The AWS region as a string, defaulting to "us-east-1".</value>
    public string CurrentRegion { get; init; } = "us-east-1";

    /// <summary>
    /// Gets or sets the base service URL for queue URLs returned by CreateQueue.
    /// When set, queue URLs will use this base instead of the default AWS format.
    /// Example: "http://localhost:5050" produces URLs like "http://localhost:5050/000000000000/my-queue".
    /// </summary>
    public Uri? ServiceUrl { get; set; }

    /// <summary>
    /// Gets or sets whether API usage tracking is enabled.
    /// When enabled, all API operations are recorded for later analysis.
    /// Disabled by default; set to true to enable tracking.
    /// </summary>
    /// <value>True if usage tracking is enabled; otherwise, false.</value>
    public bool UsageTrackingEnabled { get; set; }

    /// <summary>
    /// Gets the API usage tracker that records all operations performed against this bus.
    /// Use this to query which actions and resources have been used, and to generate IAM policies.
    /// </summary>
    public ApiUsageTracker UsageTracker { get; } = new();

    /// <summary>
    /// Gets the collection of SQS queue resources.
    /// </summary>
    /// <value>A thread-safe dictionary of SQS queue resources, keyed by queue name.</value>
    internal ConcurrentDictionary<string, SqsQueueResource> Queues { get; } = [];

    /// <summary>
    /// Gets the collection of SQS move tasks.
    /// </summary>
    /// <value>A thread-safe dictionary of SQS move tasks, keyed by task identifier.</value>
    internal ConcurrentDictionary<string, SqsMoveTask> MoveTasks { get; } = [];

    /// <summary>
    /// Gets the collection of SNS topic resources.
    /// </summary>
    /// <value>A thread-safe dictionary of SNS topic resources, keyed by topic name.</value>
    internal ConcurrentDictionary<string, SnsTopicResource> Topics { get; } = [];

    /// <summary>
    /// Gets the collection of SNS subscriptions.
    /// </summary>
    /// <value>A thread-safe dictionary of SNS subscriptions, keyed by subscription ID.</value>
    internal ConcurrentDictionary<string, SnsSubscription> Subscriptions { get; } = [];

    /// <summary>
    /// Records an API operation if usage tracking is enabled.
    /// </summary>
    /// <param name="service">The AWS service name (e.g., "sqs" or "sns").</param>
    /// <param name="action">The API action name (e.g., "SendMessage").</param>
    /// <param name="resourceArn">The ARN of the resource accessed, if applicable.</param>
    /// <param name="success">Whether the operation completed successfully.</param>
    internal void RecordOperation(string service, string action, string? resourceArn = null, bool success = true)
    {
        if (!UsageTrackingEnabled)
        {
            return;
        }

        UsageTracker.RecordOperation(new ApiOperation
        {
            Service = service,
            Action = action,
            ResourceArn = resourceArn,
            Timestamp = TimeProvider.GetUtcNow(),
            Success = success
        });
    }
}
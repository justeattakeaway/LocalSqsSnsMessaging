using System.Collections.Concurrent;
using Amazon;

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
    public RegionEndpoint CurrentRegion { get; init; } = RegionEndpoint.USEast1;

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
}
using System.Collections.Concurrent;

namespace LocalSqsSnsMessaging;

/// <summary>
/// The SQS, SNS and EventBridge resources of one region. AWS resources are regional: a request is
/// served from the resources of the region it was made in, and same-named resources in two regions
/// are separate resources.
/// </summary>
internal sealed class AwsRegionResources(string region)
{
    /// <summary>The region's name, e.g. <c>us-east-1</c>.</summary>
    public string Region { get; } = region;

    /// <summary>The region's SQS queues, keyed by queue name.</summary>
    public ConcurrentDictionary<string, SqsQueueResource> Queues { get; } = [];

    /// <summary>The region's SNS topics, keyed by topic name.</summary>
    public ConcurrentDictionary<string, SnsTopicResource> Topics { get; } = [];

    /// <summary>The subscriptions to the region's SNS topics, keyed by subscription ARN.</summary>
    public ConcurrentDictionary<string, SnsSubscription> Subscriptions { get; } = [];

    /// <summary>The region's EventBridge event buses (each holding its own rules and targets), keyed by name.</summary>
    public ConcurrentDictionary<string, EventBusResource> EventBuses { get; } = [];

    /// <summary>The region's EventBridge archives, keyed by archive name.</summary>
    public ConcurrentDictionary<string, ArchiveResource> EventBridgeArchives { get; } = [];

    /// <summary>The region's EventBridge replays (EventBridge keeps finished replays too), keyed by replay name.</summary>
    public ConcurrentDictionary<string, ReplayResource> EventBridgeReplays { get; } = [];
}

using System.Collections.Concurrent;

namespace LocalSqsSnsMessaging;

/// <summary>
/// The SQS, SNS, EventBridge and DynamoDB resources of one region. AWS resources are regional: a request is
/// served from the resources of the region it was made in, and same-named resources in two regions
/// are separate resources.
/// </summary>
/// <param name="region">The region's name.</param>
/// <param name="dynamoDbGate">The bus's DynamoDB lock, which every region's <see cref="DynamoDb"/> shares.</param>
internal sealed class AwsRegionResources(string region, object dynamoDbGate)
{
    /// <summary>The region's name, e.g. <c>us-east-1</c>.</summary>
    public string Region { get; } = region;

    /// <summary>The region's SQS queues, keyed by queue name.</summary>
    public ConcurrentDictionary<string, SqsQueueResource> Queues { get; } = [];

    /// <summary>The region's SNS topics, keyed by topic name.</summary>
    public ConcurrentDictionary<string, SnsTopicResource> Topics { get; } = [];

    /// <summary>
    /// The subscriptions to the region's SNS topics, keyed by subscription ARN: the index that resolves a
    /// subscription ARN. Each topic holds its own subscriptions and writes them here, under its gate, as
    /// they come and go; nothing else changes it.
    /// </summary>
    public ConcurrentDictionary<string, SnsSubscription> Subscriptions { get; } = [];

    /// <summary>The region's SNS platform applications (mobile push), keyed by ARN; each holds its endpoints.</summary>
    public ConcurrentDictionary<string, SnsPlatformApplication> PlatformApplications { get; } = [];

    /// <summary>The region's SNS SMS settings: account attributes, opted-out numbers and the SMS sandbox.</summary>
    public SnsSmsAccount Sms { get; } = new();

    /// <summary>The region's EventBridge event buses (each holding its own rules and targets), keyed by name.</summary>
    public ConcurrentDictionary<string, EventBusResource> EventBuses { get; } = [];

    /// <summary>The region's EventBridge archives, keyed by archive name.</summary>
    public ConcurrentDictionary<string, ArchiveResource> EventBridgeArchives { get; } = [];

    /// <summary>The region's EventBridge replays (EventBridge keeps finished replays too), keyed by replay name.</summary>
    public ConcurrentDictionary<string, ReplayResource> EventBridgeReplays { get; } = [];

    /// <summary>The EventBridge global endpoints whose home region this is, keyed by endpoint name.</summary>
    public ConcurrentDictionary<string, EndpointResource> EventBridgeEndpoints { get; } = [];

    /// <summary>The region's EventBridge connections, keyed by connection name.</summary>
    public ConcurrentDictionary<string, ConnectionResource> EventBridgeConnections { get; } = [];

    /// <summary>The region's EventBridge API destinations, keyed by name.</summary>
    public ConcurrentDictionary<string, ApiDestinationResource> EventBridgeApiDestinations { get; } = [];

    /// <summary>The region's DynamoDB tables, streams and backups, behind the bus's DynamoDB lock.</summary>
    public DynamoDb.DynamoDbStore DynamoDb { get; } = new(region, dynamoDbGate);
}

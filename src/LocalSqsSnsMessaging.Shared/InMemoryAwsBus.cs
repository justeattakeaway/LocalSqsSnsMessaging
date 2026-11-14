using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
#if NET
using System.Diagnostics;
#endif

namespace LocalSqsSnsMessaging;

/// <summary>
/// Represents an in-memory implementation of AWS messaging services (SQS and SNS).
/// </summary>
public sealed class InMemoryAwsBus
{
#if NET
    internal static readonly ActivitySource ActivitySource = new("LocalSqsSnsMessaging");
#endif

    /// <summary>
    /// Gets or initializes the TimeProvider used for time-related operations.
    /// </summary>
    /// <value>The TimeProvider instance, defaulting to TimeProvider.System.</value>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    private readonly ConcurrentDictionary<object, DateTimeOffset> _waitingReceives = new();

    /// <summary>
    /// The number of <c>ReceiveMessage</c> calls currently blocked in a long poll, waiting for a
    /// message to arrive or for their wait time to run out. Under a fake <see cref="TimeProvider"/>,
    /// these only finish once time is advanced.
    /// </summary>
    internal int WaitingReceiveCount => _waitingReceives.Count;

    /// <summary>When the soonest of the blocked long polls runs out of wait time, if any is blocked.</summary>
    internal DateTimeOffset? EarliestWaitingReceiveDeadline
    {
        get
        {
            DateTimeOffset? earliest = null;
            foreach (var deadline in _waitingReceives.Values)
            {
                if (earliest is null || deadline < earliest)
                {
                    earliest = deadline;
                }
            }

            return earliest;
        }
    }

    /// <summary>
    /// Counts a long poll that waits until <paramref name="deadline"/> at most as blocked, until the
    /// returned scope is disposed.
    /// </summary>
    internal WaitingReceiveScope EnterWaitingReceive(DateTimeOffset deadline)
    {
        var token = new object();
        _waitingReceives[token] = deadline;
        return new WaitingReceiveScope(this, token);
    }

    internal readonly struct WaitingReceiveScope(InMemoryAwsBus bus, object token) : IDisposable
    {
        public void Dispose() => bus._waitingReceives.TryRemove(token, out _);
    }

    /// <summary>
    /// Gets or initializes the current AWS account ID.
    /// </summary>
    /// <value>The AWS account ID as a string, defaulting to "000000000000".</value>
    public string CurrentAccountId { get; init; } = "000000000000";

    /// <summary>
    /// Gets or initializes the region requests are served in when they name none, and the region
    /// the clients made by <c>CreateSqsClient()</c>, <c>CreateSnsClient()</c>,
    /// <c>CreateEventBridgeClient()</c> and the raw clients use.
    /// </summary>
    /// <remarks>
    /// Resources are regional, as on AWS: an SDK request is served from the resources of the region
    /// it was signed for (or, unsigned, the region in its endpoint host), so a client configured for
    /// another region sees only that region's queues, topics and event buses.
    /// </remarks>
    /// <value>The AWS region as a string, defaulting to "us-east-1".</value>
    public string CurrentRegion { get; init; } = "us-east-1";

    /// <summary>
    /// Gets or sets the base service URL for queue URLs returned by CreateQueue.
    /// When set, queue URLs will use this base instead of the default AWS format.
    /// Example: "http://localhost:5050" produces URLs like "http://localhost:5050/000000000000/my-queue".
    /// </summary>
    public Uri? ServiceUrl { get; set; }

    /// <summary>
    /// Gets or initializes the IAM user id of the caller behind a signed SQS request, which SQS
    /// reports as a message's <c>SenderId</c> (the <c>UserId</c> <c>sts:GetCallerIdentity</c> answers).
    /// </summary>
    /// <remarks>
    /// The bus models no IAM users, so one id stands for every access key, except an access key
    /// that is a 12-digit account id: that signs as the account's root user, whose user id is the
    /// account id. Unsigned requests (as sent by <c>CreateSqsClient()</c>) report
    /// <see cref="CurrentAccountId"/>.
    /// </remarks>
    /// <value>Defaults to <c>"AIDAIOSFODNN7EXAMPLE"</c>.</value>
    public string CurrentUserId { get; init; } = "AIDAIOSFODNN7EXAMPLE";

    /// <summary>
    /// Gets or initializes the customer-managed KMS aliases that exist, for queues encrypted with
    /// <c>KmsMasterKeyId</c>. Names may be given with or without the <c>alias/</c> prefix.
    /// </summary>
    /// <remarks>
    /// The bus models no KMS keys. When this is <see langword="null"/> (the default), every key is
    /// taken to exist. When it is set, a queue whose key is a key id, a key ARN or an AWS-managed
    /// alias such as <c>alias/aws/sqs</c> still sends normally, but a queue whose key is any other
    /// alias refuses <c>SendMessage</c> with <c>KMS.NotFoundException</c>, as SQS does for a missing
    /// alias, unless that alias is listed here.
    /// </remarks>
    public ISet<string>? KmsKeyAliases { get; init; }

    private static readonly Lazy<HttpClient> DefaultHttpClient = new(() => new HttpClient());
    private HttpClient? _httpClient;

    /// <summary>
    /// Gets or sets the <see cref="System.Net.Http.HttpClient"/> used to deliver SNS notifications to
    /// <c>http</c>/<c>https</c> subscription endpoints. Defaults to a shared client that reaches the
    /// network; tests can substitute one wrapping a fake <see cref="HttpMessageHandler"/> or an
    /// ASP.NET Core test server.
    /// </summary>
    public HttpClient HttpClient
    {
        get => _httpClient ?? DefaultHttpClient.Value;
        set => _httpClient = value;
    }

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

    private readonly ConcurrentDictionary<string, AwsRegionResources> _regions = new(StringComparer.Ordinal);

    /// <summary>
    /// The resources of <paramref name="region"/>, created on first use; <see langword="null"/> or
    /// empty for <see cref="CurrentRegion"/>.
    /// </summary>
    internal AwsRegionResources Region(string? region) =>
        _regions.GetOrAdd(string.IsNullOrEmpty(region) ? CurrentRegion : region!, static name => new AwsRegionResources(name));

    /// <summary>Every region a resource has been looked up or created in.</summary>
    internal IEnumerable<AwsRegionResources> Regions => _regions.Values;

    /// <summary>
    /// The resources of the region an ARN names (<c>arn:partition:service:region:account:resource</c>),
    /// or of <see cref="CurrentRegion"/> when <paramref name="arn"/> is not an ARN naming a region.
    /// </summary>
    internal AwsRegionResources RegionOfArn(string? arn) => Region(ArnRegion(arn));

    /// <summary>The region an ARN names, or <see langword="null"/> when it is not an ARN naming one.</summary>
    internal static string? ArnRegion(string? arn)
    {
        if (arn is null || !arn.StartsWith("arn:", StringComparison.Ordinal))
        {
            return null;
        }

        var parts = arn.Split(':', 6);
        return parts.Length == 6 && parts[3].Length > 0 ? parts[3] : null;
    }

    /// <summary>Gets the SQS queues in <see cref="CurrentRegion"/>, keyed by queue name.</summary>
    internal ConcurrentDictionary<string, SqsQueueResource> Queues => Region(CurrentRegion).Queues;

    /// <summary>
    /// Finds the queue an SQS ARN names, in the region the ARN names, for the deliveries that
    /// address a queue by ARN (SNS subscriptions, EventBridge targets, dead-letter sources). The
    /// account in the ARN is not checked; a value that is not an ARN is looked up by name in
    /// <see cref="CurrentRegion"/>.
    /// </summary>
    internal bool TryGetQueueByArn(string? arn, [NotNullWhen(true)] out SqsQueueResource? queue)
    {
        queue = null;
        return arn is not null && RegionOfArn(arn).Queues.TryGetValue(arn[(arn.LastIndexOf(':') + 1)..], out queue);
    }

    /// <summary>
    /// Finds the topic an SNS ARN names, in the region the ARN names. The whole ARN must match, so a
    /// topic of the same name in another account is not found.
    /// </summary>
    internal bool TryGetTopicByArn(string? arn, [NotNullWhen(true)] out SnsTopicResource? topic)
    {
        topic = null;
        return arn is not null
               && RegionOfArn(arn).Topics.TryGetValue(arn[(arn.LastIndexOf(':') + 1)..], out topic)
               && string.Equals(topic.Arn, arn, StringComparison.Ordinal);
    }

    /// <summary>
    /// Gets the collection of SQS move tasks.
    /// </summary>
    /// <value>A thread-safe dictionary of SQS move tasks, keyed by task identifier.</value>
    internal ConcurrentDictionary<string, SqsMoveTask> MoveTasks { get; } = [];

    /// <summary>Gets the SNS topics in <see cref="CurrentRegion"/>, keyed by topic name.</summary>
    internal ConcurrentDictionary<string, SnsTopicResource> Topics => Region(CurrentRegion).Topics;

    /// <summary>Gets the subscriptions to the SNS topics in <see cref="CurrentRegion"/>, keyed by subscription ARN.</summary>
    internal ConcurrentDictionary<string, SnsSubscription> Subscriptions => Region(CurrentRegion).Subscriptions;

    /// <summary>Gets the EventBridge event buses in <see cref="CurrentRegion"/>, keyed by name.</summary>
    internal ConcurrentDictionary<string, EventBusResource> EventBuses => Region(CurrentRegion).EventBuses;

    /// <summary>Gets the EventBridge archives in <see cref="CurrentRegion"/>, keyed by name.</summary>
    internal ConcurrentDictionary<string, ArchiveResource> EventBridgeArchives => Region(CurrentRegion).EventBridgeArchives;

    /// <summary>Gets the EventBridge replays in <see cref="CurrentRegion"/>, keyed by name.</summary>
    internal ConcurrentDictionary<string, ReplayResource> EventBridgeReplays => Region(CurrentRegion).EventBridgeReplays;

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
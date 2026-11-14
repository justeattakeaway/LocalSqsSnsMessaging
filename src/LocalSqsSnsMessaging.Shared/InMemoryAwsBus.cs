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

    /// <summary>
    /// Gets or initializes whether SQS requests are served from the queues of the region they were
    /// made in, as each regional AWS endpoint does.
    /// </summary>
    /// <remarks>
    /// When <see langword="false"/> (the default), every request is served from the queues of
    /// <see cref="CurrentRegion"/>, whatever region its client was configured for. When
    /// <see langword="true"/>, the region is read from the request's SigV4 credential scope or its
    /// endpoint host: same-named queues in two regions are separate queues, a queue's ARN and URL
    /// name the region it was created in, and a client in one region can't reach another's queues.
    /// Requests that name no region, and the raw clients, use <see cref="CurrentRegion"/>.
    /// </remarks>
    public bool IsolateRegions { get; init; }

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

    /// <summary>
    /// Gets the SQS queues in <see cref="CurrentRegion"/>, the region requests are served in unless
    /// they name another.
    /// </summary>
    /// <value>A thread-safe dictionary of SQS queue resources, keyed by queue name.</value>
    internal ConcurrentDictionary<string, SqsQueueResource> Queues { get; } = [];

    /// <summary>The SQS queues in every region other than <see cref="CurrentRegion"/>, keyed by region.</summary>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, SqsQueueResource>> _otherRegionQueues = new(StringComparer.Ordinal);

    /// <summary>
    /// The SQS queues in <paramref name="region"/>, keyed by queue name, or <see langword="null"/>
    /// when no queue was ever created there. Same-named queues in two regions are separate queues.
    /// </summary>
    internal ConcurrentDictionary<string, SqsQueueResource>? FindQueuesInRegion(string region) =>
        string.Equals(region, CurrentRegion, StringComparison.Ordinal)
            ? Queues
            : _otherRegionQueues.TryGetValue(region, out var queues) ? queues : null;

    /// <summary>The SQS queues in <paramref name="region"/>, keyed by queue name, created on first use.</summary>
    internal ConcurrentDictionary<string, SqsQueueResource> GetOrAddQueuesInRegion(string region) =>
        FindQueuesInRegion(region) ?? _otherRegionQueues.GetOrAdd(region, _ => new ConcurrentDictionary<string, SqsQueueResource>());

    /// <summary>
    /// Finds the queue an SQS ARN names, in the region the ARN names, for the deliveries that
    /// address a queue by ARN (SNS subscriptions, EventBridge targets, dead-letter sources).
    /// </summary>
    /// <remarks>
    /// The account in the ARN is not checked. An ARN naming a region this bus has never created a
    /// queue in, or one that is not a well-formed ARN, is looked up by name in
    /// <see cref="CurrentRegion"/>, as it was before queues were partitioned by region.
    /// </remarks>
    internal bool TryGetQueueByArn(string? arn, [NotNullWhen(true)] out SqsQueueResource? queue)
    {
        queue = null;
        if (arn is null)
        {
            return false;
        }

        var parts = arn.Split(':');
        var queues = parts.Length == 6 ? FindQueuesInRegion(parts[3]) ?? Queues : Queues;
        return queues.TryGetValue(parts[^1], out queue);
    }

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
    /// Gets the collection of EventBridge event buses (each holding its own rules and targets).
    /// </summary>
    /// <value>A thread-safe dictionary of event buses, keyed by event bus name.</value>
    internal ConcurrentDictionary<string, EventBusResource> EventBuses { get; } = [];

    /// <summary>
    /// Gets the collection of EventBridge archives.
    /// </summary>
    /// <value>A thread-safe dictionary of archives, keyed by archive name.</value>
    internal ConcurrentDictionary<string, ArchiveResource> EventBridgeArchives { get; } = [];

    /// <summary>
    /// Gets the collection of EventBridge replays (EventBridge keeps finished replays too).
    /// </summary>
    /// <value>A thread-safe dictionary of replays, keyed by replay name.</value>
    internal ConcurrentDictionary<string, ReplayResource> EventBridgeReplays { get; } = [];

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
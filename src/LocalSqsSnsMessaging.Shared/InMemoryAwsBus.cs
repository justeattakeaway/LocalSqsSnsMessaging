using System.Collections.Concurrent;
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
    /// Gets the collection of EventBridge event buses (each holding its own rules and targets).
    /// </summary>
    /// <value>A thread-safe dictionary of event buses, keyed by event bus name.</value>
    internal ConcurrentDictionary<string, EventBusResource> EventBuses { get; } = [];

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
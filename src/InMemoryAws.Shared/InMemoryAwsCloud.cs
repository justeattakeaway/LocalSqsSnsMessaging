using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
#if NET
using System.Diagnostics;
#endif

namespace InMemoryAws;

/// <summary>
/// Represents an in-memory implementation of AWS services: SQS, SNS, EventBridge and DynamoDB.
/// </summary>
public sealed class InMemoryAwsCloud
{
#if NET
    internal static readonly ActivitySource ActivitySource = new("InMemoryAws");
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

    internal readonly struct WaitingReceiveScope(InMemoryAwsCloud cloud, object token) : IDisposable
    {
        public void Dispose() => cloud._waitingReceives.TryRemove(token, out _);
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
    /// The cloud models no IAM users, so one id stands for every access key, except an access key
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
    /// The cloud models no KMS keys. When this is <see langword="null"/> (the default), every key is
    /// taken to exist. When it is set, a queue whose key is a key id, a key ARN or an AWS-managed
    /// alias such as <c>alias/aws/sqs</c> still sends normally, but a queue whose key is any other
    /// alias refuses <c>SendMessage</c> with <c>KMS.NotFoundException</c>, as SQS does for a missing
    /// alias, unless that alias is listed here.
    /// </remarks>
    public ISet<string>? KmsKeyAliases { get; init; }

    /// <summary>
    /// Gets or initializes the secret access keys the cloud knows, by access key id, so S3 can
    /// authenticate requests signed with them.
    /// </summary>
    /// <remarks>
    /// By default (<see langword="null"/>) no signature is checked: requests may be signed with any
    /// credentials, or none. For a request signed with SigV4 (an <c>Authorization</c> header or a
    /// presigned URL) by an access key listed here, S3 recomputes the signature with its secret and
    /// refuses a mismatch (<c>SignatureDoesNotMatch</c>), refuses a request time more than 15 minutes
    /// from <see cref="TimeProvider"/>'s (<c>RequestTimeTooSkewed</c>) and a presigned URL used after
    /// it expired, and checks each chunk signature of a streamed upload. Requests signed by any other
    /// access key are served as before. Other services don't check signatures.
    /// </remarks>
    public IReadOnlyDictionary<string, string>? SecretAccessKeys { get; init; }

    /// <summary>
    /// Gets or initializes how long DynamoDB's asynchronous changes take, measured on
    /// <see cref="TimeProvider"/>: a table CreateTable or a restore makes stays <c>CREATING</c>
    /// (with its global secondary indexes), a table whose throughput or billing mode UpdateTable
    /// changes stays <c>UPDATING</c>, a global secondary index UpdateTable adds or deletes stays
    /// <c>CREATING</c> or <c>DELETING</c> (and its table <c>UPDATING</c>), a deleted table stays
    /// <c>DELETING</c>, and contributor insights stay <c>ENABLING</c> or <c>DISABLING</c>.
    /// Meanwhile DynamoDB's refusals apply: a <c>CREATING</c> table serves no reads or writes, a
    /// second throughput change or index build waits for the first, and a backup a table is
    /// being restored from can't be deleted. Defaults to zero: every change is done when its
    /// request returns, so tests needn't wait. (AWS takes seconds to minutes.)
    /// </summary>
    public TimeSpan DynamoDbTransitionDelay { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// Gets or initializes how long a DynamoDB vector index that UpdateTable adds stays
    /// <c>CREATING</c> before it becomes <c>ACTIVE</c>, measured on <see cref="TimeProvider"/>:
    /// the first half allocating resources (when the index can't be deleted), the second half
    /// backfilling (<c>Backfilling</c> is true). A <c>CREATING</c> index takes writes but can't be
    /// searched, and the table can't be deleted. Defaults to zero: the index is ACTIVE as soon as
    /// UpdateTable returns. (AWS takes minutes.) A vector index created with its table is ACTIVE
    /// when the table is.
    /// </summary>
    public TimeSpan DynamoDbVectorIndexCreationDelay { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// Gets or initializes how long the tables of an S3 Metadata configuration stay
    /// <c>CREATING</c> before they are <c>ACTIVE</c> (or <c>FAILED</c>), measured on
    /// <see cref="TimeProvider"/>. Meanwhile S3 refuses to update the configuration's tables.
    /// Defaults to zero: the tables are ACTIVE as soon as the request returns. (AWS takes about a
    /// minute.)
    /// </summary>
    public TimeSpan S3MetadataTableCreationDelay { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// Gets or initializes how often DynamoDB time to live looks for expired items in a table
    /// with time to live enabled, measured on <see cref="TimeProvider"/>. An item expires once
    /// its TTL attribute (epoch seconds) is in the past; it is deleted on the next sweep.
    /// AWS deletes expired items within a few days; this defaults to one second.
    /// </summary>
    public TimeSpan DynamoDbTimeToLiveSweepInterval { get; init; } = TimeSpan.FromSeconds(1);

    private static readonly Lazy<HttpClient> DefaultHttpClient = new(() => new HttpClient());
    private HttpClient? _httpClient;

    /// <summary>
    /// Gets or sets the <see cref="System.Net.Http.HttpClient"/> used to deliver SNS notifications to
    /// <c>http</c>/<c>https</c> subscription endpoints, to call EventBridge API destinations and to
    /// request an EventBridge OAuth connection's token. Defaults to a shared client that reaches the
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
    /// Gets the API usage tracker that records all operations performed against this cloud.
    /// Use this to query which actions and resources have been used, and to generate IAM policies.
    /// </summary>
    public ApiUsageTracker UsageTracker { get; } = new();

    private readonly ConcurrentDictionary<string, AwsRegionResources> _regions = new(StringComparer.Ordinal);

    /// <summary>
    /// The resources of <paramref name="region"/>, created on first use; <see langword="null"/> or
    /// empty for <see cref="CurrentRegion"/>.
    /// </summary>
    internal AwsRegionResources Region(string? region) =>
        _regions.GetOrAdd(string.IsNullOrEmpty(region) ? CurrentRegion : region!, static (name, gate) => new AwsRegionResources(name, gate), _dynamoDbGate);

    /// <summary>
    /// The lock every region's DynamoDB requests hold (<see cref="DynamoDb.DynamoDbStore.Gate"/>): one
    /// for the cloud, because a global table's write changes its replicas in other regions too.
    /// </summary>
    private readonly object _dynamoDbGate = new();

    /// <summary>Every region a resource has been looked up or created in.</summary>
    internal IEnumerable<AwsRegionResources> Regions => _regions.Values;

    /// <summary>Gets the SQS queues in <see cref="CurrentRegion"/>, keyed by queue name.</summary>
    internal ConcurrentDictionary<string, SqsQueueResource> Queues => Region(CurrentRegion).Queues;

    /// <summary>
    /// The resources of <paramref name="region"/> if anything has been created or looked up there,
    /// without creating them: resolving an ARN that names an unknown region finds nothing.
    /// </summary>
    internal bool TryGetRegion(string? region, [NotNullWhen(true)] out AwsRegionResources? resources)
    {
        resources = null;
        return !string.IsNullOrEmpty(region) && _regions.TryGetValue(region!, out resources);
    }

    /// <summary>
    /// Finds the queue an SQS ARN names, for the deliveries that address a queue by ARN (SNS
    /// subscriptions, EventBridge targets, dead-letter queues, move tasks). The whole ARN must
    /// match: region, account and name. A queue deleted and recreated under the same name is found,
    /// so callers resolve the ARN each time they deliver rather than keep the queue they found.
    /// </summary>
    internal bool TryGetQueueByArn(string? arn, [NotNullWhen(true)] out SqsQueueResource? queue)
    {
        queue = null;
        if (!Arn.TryParse(arn, out var parsed)
            || !TryGetRegion(parsed.Region, out var resources)
            || !resources.Queues.TryGetValue(parsed.Resource, out var found)
            || !string.Equals(found.Arn, arn, StringComparison.Ordinal))
        {
            return false;
        }

        queue = found;
        return true;
    }

    /// <summary>Finds the topic an SNS ARN names. The whole ARN must match: region, account and name.</summary>
    internal bool TryGetTopicByArn(string? arn, [NotNullWhen(true)] out SnsTopicResource? topic)
    {
        topic = null;
        if (!Arn.TryParse(arn, out var parsed)
            || !TryGetRegion(parsed.Region, out var resources)
            || !resources.Topics.TryGetValue(parsed.Resource, out var found)
            || !string.Equals(found.Arn, arn, StringComparison.Ordinal))
        {
            return false;
        }

        topic = found;
        return true;
    }

    /// <summary>
    /// Finds the event bus an EventBridge ARN (<c>...:event-bus/name</c>) names. The whole ARN must
    /// match: region, account and name.
    /// </summary>
    internal bool TryGetEventBusByArn(string? arn, [NotNullWhen(true)] out EventBusResource? eventBus)
    {
        const string prefix = "event-bus/";
        eventBus = null;
        if (!Arn.TryParse(arn, out var parsed)
            || !parsed.Resource.StartsWith(prefix, StringComparison.Ordinal)
            || !TryGetRegion(parsed.Region, out var resources)
            || !resources.EventBuses.TryGetValue(parsed.Resource[prefix.Length..], out var found)
            || !string.Equals(found.Arn, arn, StringComparison.Ordinal))
        {
            return false;
        }

        eventBus = found;
        return true;
    }

    /// <summary>
    /// Gets the collection of SQS move tasks.
    /// </summary>
    /// <value>A thread-safe dictionary of SQS move tasks, keyed by task identifier.</value>
    internal ConcurrentDictionary<string, SqsMoveTask> MoveTasks { get; } = [];

    /// <summary>
    /// The cloud's S3 buckets. Bucket names are global, so the store is cloud-wide; each bucket
    /// records the region it was created in.
    /// </summary>
    internal S3.S3Store S3 { get; } = new();

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
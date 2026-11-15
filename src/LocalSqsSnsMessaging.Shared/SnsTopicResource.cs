using LocalSqsSnsMessaging.Sns.Model;

namespace LocalSqsSnsMessaging;

/// <summary>
/// An SNS topic: its attributes and tags, its subscriptions, and its fan-out. Every change to the
/// subscription set (<see cref="Subscribe"/>, <see cref="Confirm"/>, <see cref="UpdateSubscription"/>,
/// <see cref="Unsubscribe"/>, <see cref="Delete"/>) and every publish (<see cref="Publish"/>,
/// <see cref="PublishBatch"/>) goes through the topic, which keeps the set, the region's
/// subscription index and the fan-out in step, and replays a FIFO topic's archive to a subscription
/// whose <c>ReplayPolicy</c> asks for it.
/// </summary>
/// <remarks>
/// Lock order: <see cref="SubscriptionGate"/>, then a FIFO topic's <see cref="SnsFifoTopicState.Gate"/>;
/// never the reverse. Only the topic and its fan-out take the FIFO gate. A FIFO publish runs under the
/// FIFO gate, and so never takes <see cref="SubscriptionGate"/>: what it needs of the attributes it
/// reads from <see cref="SignatureVersion"/> and <see cref="DeliveryPolicy"/>.
/// </remarks>
internal sealed class SnsTopicResource
{
    /// <summary>The message size a topic accepts when it hasn't set <c>MaximumMessageSize</c>: 256 KiB.</summary>
    public const int DefaultMaximumMessageSize = 262144;

    /// <summary>A topic accepting more than 256 KiB refuses subscriptions beyond this many.</summary>
    private const int LargeMessageSubscriptionLimit = 100;

    private readonly InMemoryAwsBus _bus;
    private readonly AwsRegionResources _region;
    private readonly SnsFanOut _fanOut;

    /// <summary>The topic's subscriptions, confirmed or not, by protocol and endpoint. Guarded by <see cref="SubscriptionGate"/>.</summary>
    private readonly Dictionary<(string Protocol, string Endpoint), SnsSubscription> _subscriptions = [];

    private volatile SnsSubscription[] _allSubscriptions = [];
    private volatile SnsSubscription[] _fanOutSubscriptions = [];
    private bool _deleted;

    /// <param name="bus">The bus the topic delivers through.</param>
    /// <param name="region">
    /// The region the topic is in. The topic adds its subscriptions to the region's
    /// <see cref="AwsRegionResources.Subscriptions"/> index, and removes them, as they come and go.
    /// </param>
    public SnsTopicResource(InMemoryAwsBus bus, AwsRegionResources region)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(region);
        _bus = bus;
        _region = region;
        _fanOut = new SnsFanOut(this, bus);
    }

    public required string Name { get; init; }
    public required string Arn { get; init; }

    /// <summary>The topic's region, e.g. <c>us-east-1</c>.</summary>
    public string Region => _region.Region;

    /// <summary>
    /// The topic's stored attributes: what the caller set, plus the defaults SNS gives every topic
    /// (<c>DisplayName</c>, <c>Policy</c>, and on a FIFO topic <c>FifoTopic</c> and
    /// <c>ContentBasedDeduplication</c>). Computed attributes such as the subscription counters are
    /// added when the attributes are read. Guarded by <see cref="SubscriptionGate"/>.
    /// </summary>
    public Dictionary<string, string> Attributes { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// A FIFO topic's deduplication and archive state, or null for a standard topic. Fixed when the
    /// topic is created, as <c>FifoTopic</c> is.
    /// </summary>
    public SnsFifoTopicState? Fifo { get; init; }

    /// <summary>Whether this is a FIFO topic (<c>FifoTopic=true</c>, and a <c>.fifo</c> name).</summary>
    public bool IsFifo => Fifo is not null;

    /// <summary>
    /// Serialises changes to the topic's subscriptions with each other and with the checks that weigh
    /// them against its <c>MaximumMessageSize</c>, so two callers can't each see room for one more
    /// subscription, or race a raised limit against a subscription the raised limit forbids. Also
    /// guards <see cref="Attributes"/> and <see cref="Tags"/>, which callers may read and change under it.
    /// See the class remarks for the lock order.
    /// </summary>
    internal object SubscriptionGate { get; } = new();

    public Dictionary<string, string> Tags { get; } = new(StringComparer.Ordinal);

    private volatile string? _signatureVersion;
    private volatile string? _deliveryPolicy;

    /// <summary>The <c>SignatureVersion</c> attribute, readable without <see cref="SubscriptionGate"/>.</summary>
    public string? SignatureVersion => _signatureVersion;

    /// <summary>The <c>DeliveryPolicy</c> attribute, readable without <see cref="SubscriptionGate"/>.</summary>
    public string? DeliveryPolicy => _deliveryPolicy;

    private int _subscriptionsDeleted;

    /// <summary>How many subscriptions have been unsubscribed from this topic: <c>SubscriptionsDeleted</c>.</summary>
    public int SubscriptionsDeleted => Volatile.Read(ref _subscriptionsDeleted);

    /// <summary>The topic's subscriptions, confirmed or pending confirmation, as of now. Readable without <see cref="SubscriptionGate"/>.</summary>
    public IReadOnlyList<SnsSubscription> Subscriptions => _allSubscriptions;

    /// <summary>
    /// The subscriptions the topic fans out to: the confirmed ones. Replaced as a whole whenever the
    /// set changes, so a publish sees a subscription either completely or not at all.
    /// </summary>
    internal IReadOnlyList<SnsSubscription> FanOutSubscriptions => _fanOutSubscriptions;

    /// <summary>
    /// The topic's effective message size limit: its <c>MaximumMessageSize</c> attribute, or 256 KiB
    /// when the topic hasn't opted in to larger payloads.
    /// </summary>
    public int MaximumMessageSize
    {
        get
        {
            lock (SubscriptionGate)
            {
                return Attributes.TryGetValue(SnsTopicAttributes.MaximumMessageSize, out var value)
                       && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size)
                    ? size
                    : DefaultMaximumMessageSize;
            }
        }
    }

    /// <summary>
    /// Subscribes <paramref name="candidate"/>'s endpoint. When the topic already has a subscription
    /// with the same protocol and endpoint, <paramref name="ensureMatchesExisting"/> is given it (and
    /// throws to refuse the request) and it is returned unchanged. Otherwise the candidate, with its
    /// attributes already applied, is added: to the topic, to the region's index and (once confirmed:
    /// an <c>sqs</c> subscription is from the start) to the fan-out, and on a FIFO topic it is replayed
    /// the archived messages its <c>ReplayPolicy</c> selects before any later publish reaches it. An
    /// HTTP/S candidate is sent its <c>SubscriptionConfirmation</c>.
    /// </summary>
    /// <exception cref="InternalNotFoundException">The topic has been deleted.</exception>
    /// <exception cref="InternalInvalidParameterException">
    /// The topic accepts messages over 256 KiB and can't take this subscription: an HTTP/S one, or
    /// one beyond its hundredth.
    /// </exception>
    public SnsSubscription Subscribe(SnsSubscription candidate, Action<SnsSubscription> ensureMatchesExisting)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(ensureMatchesExisting);

        lock (SubscriptionGate)
        {
            // A subscription added after Delete would outlive the topic.
            if (_deleted)
            {
                throw new InternalNotFoundException(InternalSnsClient.TopicDoesNotExist);
            }

            if (_subscriptions.TryGetValue((candidate.Protocol, candidate.EndPoint), out var existing))
            {
                ensureMatchesExisting(existing);
                return existing;
            }

            EnsureRoomFor(candidate.Protocol);
            _subscriptions.Add((candidate.Protocol, candidate.EndPoint), candidate);
            _region.Subscriptions[candidate.SubscriptionArn] = candidate;
            Activate(candidate);
            if (candidate.IsHttp)
            {
                SnsHttpDelivery.SendSubscriptionConfirmation(_bus, candidate);
            }

            return candidate;
        }
    }

    /// <summary>
    /// Confirms the subscription <paramref name="token"/> was issued to, bringing it into the fan-out
    /// (and replaying to it, as <see cref="Subscribe"/> does). A subscription already confirmed is
    /// left as it is. Null when no subscription of this topic has that token.
    /// </summary>
    public SnsSubscription? Confirm(string? token)
    {
        lock (SubscriptionGate)
        {
            var subscription = _allSubscriptions.FirstOrDefault(s =>
                string.Equals(s.ConfirmationToken, token, StringComparison.Ordinal));
            if (subscription is { PendingConfirmation: true })
            {
                subscription.PendingConfirmation = false;
                Activate(subscription);
            }

            return subscription;
        }
    }

    /// <summary>
    /// Changes <paramref name="subscription"/> through <paramref name="update"/> (which validates and
    /// throws to refuse), under the gate that subscription changes take. A change that leaves a replay
    /// pending (a new <c>ReplayPolicy</c>) starts it straight away.
    /// </summary>
    /// <exception cref="InternalNotFoundException">The subscription has been unsubscribed, or its topic deleted.</exception>
    public void UpdateSubscription(SnsSubscription subscription, Action<SnsSubscription> update)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(update);

        lock (SubscriptionGate)
        {
            if (!IsMember(subscription))
            {
                throw new InternalNotFoundException(InternalSnsClient.SubscriptionDoesNotExist);
            }

            update(subscription);
            if (subscription.ReplayStatus == SnsReplayPolicy.Pending)
            {
                _fanOut.Replay(subscription);
            }
        }
    }

    /// <summary>
    /// Removes <paramref name="subscription"/> from the topic, the region's index and the fan-out, and
    /// counts it in <c>SubscriptionsDeleted</c>. Does nothing for a subscription the topic no longer has.
    /// </summary>
    public void Unsubscribe(SnsSubscription subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        lock (SubscriptionGate)
        {
            if (!IsMember(subscription))
            {
                return;
            }

            _subscriptions.Remove((subscription.Protocol, subscription.EndPoint));
            _region.Subscriptions.TryRemove(subscription.SubscriptionArn, out _);
            Interlocked.Increment(ref _subscriptionsDeleted);
            Rebuild();
        }
    }

    /// <summary>
    /// Deletes the topic's subscriptions with it (without counting them as unsubscribed), and refuses
    /// any later <see cref="Subscribe"/>. The caller has already taken the topic off the region.
    /// </summary>
    public void Delete()
    {
        lock (SubscriptionGate)
        {
            _deleted = true;
            foreach (var subscription in _subscriptions.Values)
            {
                _region.Subscriptions.TryRemove(subscription.SubscriptionArn, out _);
            }

            _subscriptions.Clear();
            Rebuild();
        }
    }

    /// <summary>
    /// Publishes a message to the topic's confirmed subscriptions. The request has been validated
    /// against the topic already.
    /// </summary>
    public PublishResponse Publish(PublishRequest request) => _fanOut.Execute(request);

    /// <summary>
    /// Publishes each entry of a batch to the topic's confirmed subscriptions. The request has been
    /// validated against the topic already.
    /// </summary>
    public PublishBatchResponse PublishBatch(PublishBatchRequest request) => _fanOut.ExecuteBatch(request);

    /// <summary>
    /// A topic accepting more than 256 KiB only supports SQS subscriptions (AWS also allows Firehose
    /// and Lambda, neither of which this bus supports), so raising <c>MaximumMessageSize</c> above
    /// 256 KiB is refused while it has any other. AWS checks only the protocols here: raising the limit
    /// on a topic that already has more than 100 subscriptions is allowed, and only the next
    /// <see cref="Subscribe"/> is turned away. The caller holds <see cref="SubscriptionGate"/> until it
    /// has stored the new limit.
    /// </summary>
    public void EnsureSubscriptionsSupportLargeMessages()
    {
        lock (SubscriptionGate)
        {
            // Unconfirmed HTTP/S subscriptions count too, as on AWS.
            if (_subscriptions.Values.FirstOrDefault(s => !s.IsSqs) is { } unsupported)
            {
                throw new InternalInvalidParameterException(UnsupportedProtocolMessage(unsupported.Protocol));
            }
        }
    }

    /// <summary>
    /// Stores attribute <paramref name="name"/> (or removes it, for a value that clears it) and applies
    /// it to the FIFO state it governs. The caller holds <see cref="SubscriptionGate"/>, or owns a topic
    /// that isn't on the bus yet.
    /// </summary>
    public void SetAttribute(string name, string? value, DateTimeOffset now)
    {
        if (SnsTopicAttributes.ClearsAttribute(name, value))
        {
            Attributes.Remove(name);
        }
        else
        {
            Attributes[name] = value ?? string.Empty;
        }

        _signatureVersion = Attributes.GetValueOrDefault(SnsTopicAttributes.SignatureVersion);
        _deliveryPolicy = Attributes.GetValueOrDefault(SnsTopicAttributes.DeliveryPolicy);

        if (Fifo is not { } fifo)
        {
            return;
        }

        lock (fifo.Gate)
        {
            switch (name)
            {
                case SnsTopicAttributes.ContentBasedDeduplication:
                    fifo.ContentBasedDeduplication = string.Equals(value, "true", StringComparison.Ordinal);
                    break;

                case SnsTopicAttributes.FifoThroughputScope:
                    fifo.DeduplicatePerMessageGroup = string.Equals(value, "MessageGroup", StringComparison.Ordinal);
                    break;

                case SnsTopicAttributes.ArchivePolicy:
                    if (SnsTopicAttributes.ArchiveRetention(value) is { } retention)
                    {
                        fifo.StartArchiving(retention, now);
                        Attributes[SnsTopicAttributes.BeginningArchiveTime] =
                            SnsTopicAttributes.FormatTimestamp(fifo.BeginningArchiveTime!.Value);
                    }
                    else
                    {
                        // An empty policy (or {}) turns archiving off.
                        fifo.StopArchiving();
                        Attributes.Remove(SnsTopicAttributes.ArchivePolicy);
                        Attributes.Remove(SnsTopicAttributes.BeginningArchiveTime);
                    }

                    break;
            }
        }
    }

    internal static string UnsupportedProtocolMessage(string protocol) =>
        $"Invalid parameter: {SnsTopicAttributes.MaximumMessageSize} greater than {DefaultMaximumMessageSize} bytes " +
        $"is not supported for the following protocol: [{protocol}]";

    /// <summary>Whether <paramref name="subscription"/> is (still) one of this topic's subscriptions. Under the gate.</summary>
    private bool IsMember(SnsSubscription subscription) =>
        _subscriptions.TryGetValue((subscription.Protocol, subscription.EndPoint), out var member)
        && ReferenceEquals(member, subscription);

    /// <summary>
    /// The subscription side of the large-message constraint: a topic that accepts more than 256 KiB
    /// can't take an HTTP/S subscription, or a subscription beyond its hundredth. Under the gate.
    /// </summary>
    private void EnsureRoomFor(string protocol)
    {
        if (MaximumMessageSize <= DefaultMaximumMessageSize)
        {
            return;
        }

        if (protocol is "http" or "https")
        {
            throw new InternalInvalidParameterException(UnsupportedProtocolMessage(protocol));
        }

        if (_subscriptions.Count >= LargeMessageSubscriptionLimit)
        {
            throw new InternalInvalidParameterException(
                $"Invalid parameter: A topic with {SnsTopicAttributes.MaximumMessageSize} greater than {DefaultMaximumMessageSize} bytes " +
                $"supports a maximum of {LargeMessageSubscriptionLimit} subscriptions");
        }
    }

    /// <summary>
    /// Brings the subscription set's latest change into the fan-out, then replays to
    /// <paramref name="subscription"/> what its <c>ReplayPolicy</c> selects. On a FIFO topic both
    /// happen under the FIFO gate, so no publish lands between the replayed messages and the live ones
    /// that follow them. Under <see cref="SubscriptionGate"/>.
    /// </summary>
    private void Activate(SnsSubscription subscription)
    {
        if (Fifo is not { } fifo)
        {
            Rebuild();
            return;
        }

        lock (fifo.Gate)
        {
            Rebuild();
            _fanOut.Replay(subscription);
        }
    }

    /// <summary>
    /// Replaces the snapshots of the subscription set. Under <see cref="SubscriptionGate"/>. Each
    /// snapshot is a new array, so a publish already delivering keeps the set it started with.
    /// </summary>
    private void Rebuild()
    {
        var all = _subscriptions.Values.ToArray();
        _allSubscriptions = all;
        _fanOutSubscriptions = Array.FindAll(all, s => !s.PendingConfirmation);
    }
}

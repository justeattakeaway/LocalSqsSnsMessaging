namespace LocalSqsSnsMessaging;

internal sealed class SnsTopicResource
{
    public required string Name { get; init; }
    public required string Region { get; init; }
    public required string Arn { get; init; }

    /// <summary>
    /// The topic's stored attributes: what the caller set, plus the defaults SNS gives every topic
    /// (<c>DisplayName</c>, <c>Policy</c>, and on a FIFO topic <c>FifoTopic</c> and
    /// <c>ContentBasedDeduplication</c>). Computed attributes such as the subscription counters are
    /// added when the attributes are read. Guarded by <see cref="SubscriptionGate"/>.
    /// </summary>
    public Dictionary<string, string> Attributes { get; } = new(StringComparer.Ordinal);

    /// <summary>Whether this is a FIFO topic (<c>FifoTopic=true</c>, and a <c>.fifo</c> name).</summary>
    public bool IsFifo => Attributes.TryGetValue(SnsTopicAttributes.FifoTopic, out var fifo)
                          && string.Equals(fifo, "true", StringComparison.Ordinal);

    /// <summary>
    /// Serialises the checks that weigh a topic's subscriptions against its <c>MaximumMessageSize</c>
    /// with the changes that would invalidate them, so two callers can't each see room for one more
    /// subscription, or race a raised limit against a subscription the raised limit forbids. Also
    /// guards <see cref="Attributes"/> and <see cref="Tags"/>.
    /// </summary>
    internal object SubscriptionGate { get; } = new();
    internal SnsPublishAction PublishAction { get; set; } = SnsPublishAction.NullInstance;
    public Dictionary<string, string> Tags { get; } = new(StringComparer.Ordinal);

    private int _subscriptionsDeleted;

    /// <summary>How many subscriptions have been unsubscribed from this topic: <c>SubscriptionsDeleted</c>.</summary>
    public int SubscriptionsDeleted => Volatile.Read(ref _subscriptionsDeleted);

    public void RecordSubscriptionDeleted() => Interlocked.Increment(ref _subscriptionsDeleted);
}

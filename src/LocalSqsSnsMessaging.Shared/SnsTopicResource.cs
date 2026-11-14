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

    /// <summary>
    /// A FIFO topic's deduplication and archive state, or null for a standard topic. Fixed when the
    /// topic is created, as <c>FifoTopic</c> is.
    /// </summary>
    public SnsFifoTopicState? Fifo { get; init; }

    /// <summary>Whether this is a FIFO topic (<c>FifoTopic=true</c>, and a <c>.fifo</c> name).</summary>
    public bool IsFifo => Fifo is not null;

    /// <summary>
    /// Serialises the checks that weigh a topic's subscriptions against its <c>MaximumMessageSize</c>
    /// with the changes that would invalidate them, so two callers can't each see room for one more
    /// subscription, or race a raised limit against a subscription the raised limit forbids. Also
    /// guards <see cref="Attributes"/> and <see cref="Tags"/>.
    /// </summary>
    internal object SubscriptionGate { get; } = new();

    /// <summary>
    /// Fans published messages out to the topic's confirmed subscriptions. <c>CreateTopic</c> sets the
    /// first one, and it is replaced whenever the subscriptions change.
    /// </summary>
    internal SnsPublishAction PublishAction { get; set; } = null!;

    public Dictionary<string, string> Tags { get; } = new(StringComparer.Ordinal);

    private int _subscriptionsDeleted;

    /// <summary>How many subscriptions have been unsubscribed from this topic: <c>SubscriptionsDeleted</c>.</summary>
    public int SubscriptionsDeleted => Volatile.Read(ref _subscriptionsDeleted);

    public void RecordSubscriptionDeleted() => Interlocked.Increment(ref _subscriptionsDeleted);

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
}

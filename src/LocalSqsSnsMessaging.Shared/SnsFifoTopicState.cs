namespace LocalSqsSnsMessaging;

/// <summary>
/// What a FIFO topic keeps between publishes: the settings that govern deduplication and archiving,
/// the deduplication ids it has accepted, and its message archive. Publishing, replaying the archive
/// and changing these settings all take <see cref="Gate"/>, so every message in a group is sequenced,
/// deduplicated and handed to each subscription in one order.
/// </summary>
internal sealed class SnsFifoTopicState
{
    private readonly SqsDeduplicationCache _topicDeduplication = new();
    private readonly Dictionary<string, SqsDeduplicationCache> _groupDeduplication = new(StringComparer.Ordinal);
    private readonly Queue<ArchivedMessage> _archive = new();
    private TimeSpan _archiveRetention;

    /// <summary>A message in the archive, and when it was published.</summary>
    internal readonly record struct ArchivedMessage(SnsPublishAction.OutboundMessage Message, DateTimeOffset PublishedAt);

    /// <summary>Serialises publishes, replays and setting changes on the topic.</summary>
    public object Gate { get; } = new();

    /// <summary>
    /// <c>ContentBasedDeduplication</c>: a publish without a <c>MessageDeduplicationId</c> is
    /// deduplicated by the SHA-256 of its body.
    /// </summary>
    public bool ContentBasedDeduplication { get; set; }

    /// <summary>
    /// <c>FifoThroughputScope=MessageGroup</c>: a deduplication id only collides with ids accepted in
    /// the same message group, rather than anywhere on the topic (<c>Topic</c>, the default).
    /// </summary>
    public bool DeduplicatePerMessageGroup { get; set; }

    /// <summary>When the topic began archiving (<c>BeginningArchiveTime</c>), or null while it has no <c>ArchivePolicy</c>.</summary>
    public DateTimeOffset? BeginningArchiveTime { get; private set; }

    /// <summary>The deduplication ids a message published to <paramref name="messageGroupId"/> must not collide with.</summary>
    public SqsDeduplicationCache DeduplicationFor(string messageGroupId)
    {
        if (!DeduplicatePerMessageGroup)
        {
            return _topicDeduplication;
        }

        if (!_groupDeduplication.TryGetValue(messageGroupId, out var cache))
        {
            cache = new SqsDeduplicationCache();
            _groupDeduplication[messageGroupId] = cache;
        }

        return cache;
    }

    /// <summary>
    /// Archives every message published from now on for <paramref name="retention"/>. A topic that is
    /// already archiving keeps its archive and its <see cref="BeginningArchiveTime"/>; only the
    /// retention period changes.
    /// </summary>
    public void StartArchiving(TimeSpan retention, DateTimeOffset now)
    {
        // SNS reports the start to the millisecond, and a replay starting there must include it.
        BeginningArchiveTime ??= new DateTimeOffset(now.UtcTicks - now.UtcTicks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero);
        _archiveRetention = retention;
    }

    /// <summary>Stops archiving and discards the archive.</summary>
    public void StopArchiving()
    {
        BeginningArchiveTime = null;
        _archive.Clear();
    }

    /// <summary>Adds a published message to the archive, if the topic is archiving.</summary>
    public void Archive(SnsPublishAction.OutboundMessage message, DateTimeOffset publishedAt)
    {
        if (BeginningArchiveTime is null)
        {
            return;
        }

        DiscardExpired(publishedAt);
        _archive.Enqueue(new ArchivedMessage(message, publishedAt));
    }

    /// <summary>
    /// The archived messages published from <paramref name="start"/> up to <paramref name="end"/>
    /// (or the latest, when null), in publish order.
    /// </summary>
    public List<SnsPublishAction.OutboundMessage> Archived(DateTimeOffset start, DateTimeOffset? end, DateTimeOffset now)
    {
        DiscardExpired(now);
        return _archive
            .Where(archived => archived.PublishedAt >= start && (end is null || archived.PublishedAt <= end))
            .Select(archived => archived.Message)
            .ToList();
    }

    private void DiscardExpired(DateTimeOffset now)
    {
        while (_archive.Count > 0 && now - _archive.Peek().PublishedAt >= _archiveRetention)
        {
            _archive.Dequeue();
        }
    }
}

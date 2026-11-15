namespace LocalSqsSnsMessaging.DynamoDb;

/// <summary>One stream record: an item change as the stream's view type shows it.</summary>
internal sealed record DynamoStreamRecord(
    string EventId,
    string EventName,
    string SequenceNumber,
    DateTimeOffset CreatedAt,
    Dictionary<string, DynamoValue> Keys,
    IReadOnlyDictionary<string, DynamoValue>? NewImage,
    IReadOnlyDictionary<string, DynamoValue>? OldImage,
    long SizeBytes,
    bool ByTimeToLive);

/// <summary>
/// A table's DynamoDB stream: a change record per item write, in write order, in one shard.
/// Disabling the stream closes its shard, which stays readable; enabling it again makes a new
/// stream with a new label. Records are kept for 24 hours, and a disabled stream for 24 hours
/// after it is disabled.
/// </summary>
/// <remarks>
/// A record's position counts every record the shard has had, trimmed ones included, so a shard
/// iterator's position stays put as old records are trimmed.
/// </remarks>
internal sealed class DynamoStream
{
    /// <summary>How long DynamoDB Streams keeps a record, and a disabled stream.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    private static long _sequence;
    private readonly List<DynamoStreamRecord> _records = [];
    private int _trimmed;

    public DynamoStream(DynamoTable table, string viewType, DateTimeOffset createdAt, string region)
    {
        TableName = table.Name;
        KeySchema = table.KeySchema;
        ViewType = viewType;
        CreatedAt = createdAt;
        Region = region;
        Label = LabelFor(createdAt);
        Arn = ArnFor(table.Arn, createdAt);
        ShardId = $"shardId-{createdAt.ToUnixTimeMilliseconds().ToString("D20", CultureInfo.InvariantCulture)}-{Guid.NewGuid().ToString("N")[..8]}";
        StartingSequenceNumber = NextSequenceNumber();
    }

    /// <summary>The ARN of a stream of the table <paramref name="tableArn"/> created at <paramref name="createdAt"/>.</summary>
    public static string ArnFor(string tableArn, DateTimeOffset createdAt) => $"{tableArn}/stream/{LabelFor(createdAt)}";

    private static string LabelFor(DateTimeOffset createdAt) =>
        createdAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture);

    public string TableName { get; }

    /// <summary>The stream's own resource policy, apart from its table's.</summary>
    public DynamoResourcePolicy ResourcePolicy { get; } = new();

    /// <summary>The stream's own tags, apart from its table's.</summary>
    public List<KeyValuePair<string, string>> Tags { get; } = [];

    public DynamoKeySchema KeySchema { get; }

    public string ViewType { get; }

    public DateTimeOffset CreatedAt { get; }

    public string Region { get; }

    public string Label { get; }

    public string Arn { get; }

    public string ShardId { get; }

    public string StartingSequenceNumber { get; }

    public string? EndingSequenceNumber { get; private set; }

    public bool Enabled => EndingSequenceNumber is null;

    /// <summary>When the stream was disabled, if it has been.</summary>
    public DateTime? DisabledAt { get; private set; }

    /// <summary>The position of the oldest record still kept (the trim horizon).</summary>
    public int FirstPosition => _trimmed;

    /// <summary>The position after the latest record.</summary>
    public int EndPosition => _trimmed + _records.Count;

    /// <summary>Up to <paramref name="limit"/> records from <paramref name="position"/> on (which must not be before <see cref="FirstPosition"/>).</summary>
    public List<DynamoStreamRecord> From(int position, int limit) =>
        _records.Skip(position - _trimmed).Take(limit).ToList();

    /// <summary>The position of the kept record with sequence number <paramref name="sequenceNumber"/>, if there is one.</summary>
    public int? PositionOf(string sequenceNumber)
    {
        var at = _records.FindIndex(r => string.Equals(r.SequenceNumber, sequenceNumber, StringComparison.Ordinal));
        return at < 0 ? null : _trimmed + at;
    }

    /// <summary>Whether the stream was disabled long enough ago that DynamoDB has deleted it.</summary>
    public bool IsExpired(DateTimeOffset now) => DisabledAt is { } disabledAt && now.UtcDateTime - disabledAt >= Retention;

    /// <summary>Drops the records older than the retention period.</summary>
    public void Trim(DateTimeOffset now)
    {
        var count = 0;
        while (count < _records.Count && now - _records[count].CreatedAt >= Retention)
        {
            count++;
        }

        _records.RemoveRange(0, count);
        _trimmed += count;
    }

    /// <summary>Sequence numbers are numeric strings of one width, so they order as text does.</summary>
    private static string NextSequenceNumber() =>
        "1" + Interlocked.Increment(ref _sequence).ToString("D20", CultureInfo.InvariantCulture);

    public void Append(DynamoItemChange change, DynamoTable table, DateTimeOffset now)
    {
        if (!Enabled || (change.OldImage is null && change.NewImage is null))
        {
            return;
        }

        // "If you perform a PutItem or UpdateItem operation that does not change any data in an
        // item, DynamoDB Streams does not write a stream record for that operation."
        if (change.OldImage is { } before && change.NewImage is { } after && SameAttributes(before, after))
        {
            return;
        }

        var eventName = change.OldImage is null ? "INSERT" : change.NewImage is null ? "REMOVE" : "MODIFY";
        var keys = table.KeySchema.ToAttributes(change.Key);
        var newImage = ViewType is "NEW_IMAGE" or "NEW_AND_OLD_IMAGES" ? change.NewImage : null;
        var oldImage = ViewType is "OLD_IMAGE" or "NEW_AND_OLD_IMAGES" ? change.OldImage : null;
        var size = DynamoValueRules.ItemSize(keys)
            + (newImage is null ? 0 : DynamoValueRules.ItemSize(newImage))
            + (oldImage is null ? 0 : DynamoValueRules.ItemSize(oldImage));
        Trim(now);
        _records.Add(new DynamoStreamRecord(
            Guid.NewGuid().ToString("N"),
            eventName,
            NextSequenceNumber(),
            now,
            keys,
            newImage,
            oldImage,
            size,
            change.ByTimeToLive));
    }

    private static bool SameAttributes(IReadOnlyDictionary<string, DynamoValue> one, IReadOnlyDictionary<string, DynamoValue> two) =>
        one.Count == two.Count
        && one.All(attribute => two.TryGetValue(attribute.Key, out var other) && attribute.Value.Equals(other));

    public void Disable(DateTime now)
    {
        if (Enabled)
        {
            EndingSequenceNumber = _records.Count > 0 ? _records[^1].SequenceNumber : NextSequenceNumber();
            DisabledAt = now;
        }
    }
}

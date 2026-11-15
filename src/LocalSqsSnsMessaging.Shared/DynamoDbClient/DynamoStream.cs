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
/// stream with a new label.
/// </summary>
internal sealed class DynamoStream
{
    private static long _sequence;
    private readonly List<DynamoStreamRecord> _records = [];

    public DynamoStream(DynamoTable table, string viewType, DateTimeOffset createdAt, string region)
    {
        TableName = table.Name;
        KeySchema = table.KeySchema;
        ViewType = viewType;
        CreatedAt = createdAt;
        Region = region;
        Label = createdAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture);
        Arn = $"{table.Arn}/stream/{Label}";
        ShardId = $"shardId-{createdAt.ToUnixTimeMilliseconds().ToString("D20", CultureInfo.InvariantCulture)}-{Guid.NewGuid().ToString("N")[..8]}";
        StartingSequenceNumber = NextSequenceNumber();
    }

    public string TableName { get; }

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

    public IReadOnlyList<DynamoStreamRecord> Records => _records;

    /// <summary>Sequence numbers are numeric strings of one width, so they order as text does.</summary>
    private static string NextSequenceNumber() =>
        "1" + Interlocked.Increment(ref _sequence).ToString("D20", CultureInfo.InvariantCulture);

    public void Append(DynamoItemChange change, DynamoTable table, DateTimeOffset now)
    {
        if (!Enabled || (change.OldImage is null && change.NewImage is null))
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

    public void Disable(DateTime now)
    {
        if (Enabled)
        {
            EndingSequenceNumber = _records.Count > 0 ? _records[^1].SequenceNumber : NextSequenceNumber();
        }

        _ = now;
    }
}

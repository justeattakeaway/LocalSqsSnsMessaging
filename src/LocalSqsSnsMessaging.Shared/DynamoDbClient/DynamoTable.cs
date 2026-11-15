namespace LocalSqsSnsMessaging.DynamoDb;

/// <summary>One attribute of a key schema: its name and declared scalar type.</summary>
internal sealed record DynamoKeyAttribute(string Name, DynamoType Type);

/// <summary>A table's or an index's key schema: a partition (HASH) key and an optional sort (RANGE) key.</summary>
internal sealed record DynamoKeySchema(DynamoKeyAttribute Partition, DynamoKeyAttribute? Sort)
{
    public IEnumerable<DynamoKeyAttribute> Attributes =>
        Sort is null ? [Partition] : [Partition, Sort];

    public bool IsKeyAttribute(string name) =>
        string.Equals(Partition.Name, name, StringComparison.Ordinal)
        || (Sort is not null && string.Equals(Sort.Name, name, StringComparison.Ordinal));

    /// <summary>The key of <paramref name="item"/> under this schema, or null when it lacks a key attribute or has one of the wrong type.</summary>
    public DynamoKey? KeyOf(IReadOnlyDictionary<string, DynamoValue> item)
    {
        if (!item.TryGetValue(Partition.Name, out var partition) || partition.Type != Partition.Type)
        {
            return null;
        }

        DynamoValue? sort = null;
        if (Sort is not null && (!item.TryGetValue(Sort.Name, out sort) || sort.Type != Sort.Type))
        {
            return null;
        }

        return new DynamoKey(partition, sort);
    }

    /// <summary>The key attributes of <paramref name="key"/> as an item fragment.</summary>
    public Dictionary<string, DynamoValue> ToAttributes(DynamoKey key)
    {
        var attributes = new Dictionary<string, DynamoValue>(StringComparer.Ordinal) { [Partition.Name] = key.Partition };
        if (Sort is not null && key.Sort is not null)
        {
            attributes[Sort.Name] = key.Sort;
        }

        return attributes;
    }
}

/// <summary>Which attributes an index's items carry besides the keys.</summary>
internal sealed record DynamoProjection(string Type, IReadOnlyList<string>? NonKeyAttributes)
{
    public static DynamoProjection All { get; } = new("ALL", null);
}

/// <summary>A provisioned throughput setting, with when it last changed.</summary>
internal sealed class DynamoThroughput
{
    public long ReadCapacityUnits { get; set; }

    public long WriteCapacityUnits { get; set; }

    public DateTime? LastIncreaseDateTime { get; set; }

    public DateTime? LastDecreaseDateTime { get; set; }

    public long NumberOfDecreasesToday { get; set; }
}

/// <summary>
/// A global or local secondary index, kept sorted by its key so it can be queried. Entries are
/// sparse: an item is in the index only when it has every index key attribute.
/// </summary>
internal sealed class DynamoIndex
{
    private readonly Dictionary<DynamoKey, DynamoIndexEntry> _byTableKey = [];

    public required string Name { get; init; }

    public required string Arn { get; init; }

    public required bool IsGlobal { get; init; }

    public required DynamoKeySchema KeySchema { get; init; }

    public required DynamoProjection Projection { get; set; }

    /// <summary>A global index's own throughput; null for a local index or an on-demand table.</summary>
    public DynamoThroughput? Throughput { get; set; }

    public long? MaxReadRequestUnits { get; set; }

    public long? MaxWriteRequestUnits { get; set; }

    public string Status { get; set; } = "ACTIVE";

    public SortedSet<DynamoIndexEntry> Entries { get; } = new(DynamoIndexEntry.Comparer);

    public long SizeBytes { get; set; }

    public void Remove(DynamoKey tableKey, IReadOnlyDictionary<string, DynamoValue> item)
    {
        if (_byTableKey.Remove(tableKey, out var entry))
        {
            Entries.Remove(entry);
            SizeBytes -= DynamoValueRules.ItemSize(item);
        }
    }

    public void Add(DynamoKey tableKey, IReadOnlyDictionary<string, DynamoValue> item)
    {
        if (KeySchema.KeyOf(item) is { } indexKey)
        {
            var entry = new DynamoIndexEntry(indexKey, tableKey);
            _byTableKey[tableKey] = entry;
            Entries.Add(entry);
            SizeBytes += DynamoValueRules.ItemSize(item);
        }
    }

    public void Clear()
    {
        _byTableKey.Clear();
        Entries.Clear();
        SizeBytes = 0;
    }

    public int ItemCount => Entries.Count;
}

/// <summary>An item write, as streams, TTL and <c>ReturnValues</c> see it: the item before and after.</summary>
internal readonly record struct DynamoItemChange(
    DynamoKey Key,
    IReadOnlyDictionary<string, DynamoValue>? OldImage,
    IReadOnlyDictionary<string, DynamoValue>? NewImage,
    bool ByTimeToLive = false);

/// <summary>
/// A DynamoDB table: its settings, its items sorted by primary key, and its secondary indexes,
/// which every write keeps up to date. Guarded by its region's <see cref="DynamoDbStore.Gate"/>.
/// </summary>
internal sealed class DynamoTable
{
    private readonly Dictionary<DynamoKey, Dictionary<string, DynamoValue>> _items = [];

    public required string Name { get; init; }

    public required string Arn { get; init; }

    public required string TableId { get; init; }

    public required DateTime CreationDateTime { get; init; }

    public required DynamoKeySchema KeySchema { get; init; }

    /// <summary>Every attribute the table and its indexes declare a type for, in declaration order.</summary>
    public required List<DynamoKeyAttribute> AttributeDefinitions { get; set; }

    public string Status { get; set; } = "ACTIVE";

    /// <summary><c>PROVISIONED</c> or <c>PAY_PER_REQUEST</c>.</summary>
    public string BillingMode { get; set; } = "PROVISIONED";

    /// <summary>Whether the request that created (or last updated) the table named a billing mode, which DescribeTable then summarizes.</summary>
    public bool BillingModeStated { get; set; }

    public DateTime? LastUpdateToPayPerRequestDateTime { get; set; }

    public DynamoThroughput Throughput { get; set; } = new();

    public long? MaxReadRequestUnits { get; set; }

    public long? MaxWriteRequestUnits { get; set; }

    public List<DynamoIndex> GlobalIndexes { get; } = [];

    public List<DynamoIndex> LocalIndexes { get; } = [];

    public IEnumerable<DynamoIndex> Indexes => LocalIndexes.Concat(GlobalIndexes);

    public bool StreamEnabled { get; set; }

    public string? StreamViewType { get; set; }

    /// <summary>The table's current stream, and the ones it had before (DynamoDB keeps a disabled stream readable for 24 hours).</summary>
    public DynamoStream? LatestStream { get; set; }

    public string? TimeToLiveAttribute { get; set; }

    public bool TimeToLiveEnabled { get; set; }

    /// <summary>When UpdateTimeToLive last changed the setting; DynamoDB allows one change an hour.</summary>
    public DateTimeOffset? TimeToLiveModifiedAt { get; set; }

    public List<KeyValuePair<string, string>> Tags { get; } = [];

    public bool DeletionProtectionEnabled { get; set; }

    public string? TableClass { get; set; }

    public DateTime? TableClassLastUpdate { get; set; }

    /// <summary>When UpdateTable last changed the table class, for DynamoDB's limit of two changes in 30 days.</summary>
    public List<DateTime> TableClassUpdates { get; } = [];

    public bool SseEnabled { get; set; }

    public string? SseType { get; set; }

    public string? SseKmsKeyArn { get; set; }

    public string? ResourcePolicy { get; set; }

    public string? ResourcePolicyRevision { get; set; }

    public bool PointInTimeRecoveryEnabled { get; set; }

    public int PointInTimeRecoveryPeriodInDays { get; set; } = 35;

    public DateTime? EarliestRestorableDateTime { get; set; }

    /// <summary>While point-in-time recovery is on, every item change, so the table can be restored as it stood at an earlier time.</summary>
    public DynamoPointInTimeLog? PointInTimeLog { get; set; }

    /// <summary>Where a restored table was restored from, which DescribeTable reports as its <c>RestoreSummary</c>.</summary>
    public DynamoRestore? Restore { get; set; }

    /// <summary>Contributor insights settings, by index name (empty for the table itself).</summary>
    public Dictionary<string, DynamoContributorInsights> ContributorInsights { get; } = new(StringComparer.Ordinal);

    /// <summary>When the table becomes ACTIVE, if it was created with a creation delay.</summary>
    public DateTimeOffset? ActiveAt { get; set; }

    public SortedSet<DynamoKey> Keys { get; } = new(DynamoKey.Comparer);

    public long SizeBytes { get; private set; }

    public int ItemCount => _items.Count;

    public DynamoIndex? FindIndex(string name) =>
        Indexes.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.Ordinal));

    public DynamoType? DeclaredType(string attribute) =>
        AttributeDefinitions.FirstOrDefault(a => string.Equals(a.Name, attribute, StringComparison.Ordinal))?.Type;

    public Dictionary<string, DynamoValue>? Get(DynamoKey key) => _items.TryGetValue(key, out var item) ? item : null;

    /// <summary>Every item, in key order.</summary>
    public IEnumerable<(DynamoKey Key, Dictionary<string, DynamoValue> Item)> All() =>
        Keys.Select(k => (k, _items[k]));

    /// <summary>Stores <paramref name="item"/> under <paramref name="key"/>, replacing any item there.</summary>
    public DynamoItemChange Put(DynamoKey key, Dictionary<string, DynamoValue> item)
    {
        var old = Remove(key);
        PointInTimeLog?.Record(key, old);
        _items[key] = item;
        Keys.Add(key);
        SizeBytes += DynamoValueRules.ItemSize(item);
        foreach (var index in Indexes)
        {
            index.Add(key, item);
        }

        return new DynamoItemChange(key, old, item);
    }

    public DynamoItemChange Delete(DynamoKey key, bool byTimeToLive = false)
    {
        var old = Remove(key);
        if (old is not null)
        {
            PointInTimeLog?.Record(key, old);
        }

        return new(key, old, null, byTimeToLive);
    }

    /// <summary>The table's items as they stood at <paramref name="time"/>: today's, with every later change undone.</summary>
    public Dictionary<DynamoKey, Dictionary<string, DynamoValue>> ItemsAt(DateTime time)
    {
        var items = new Dictionary<DynamoKey, Dictionary<string, DynamoValue>>(_items);
        if (PointInTimeLog is { } log)
        {
            foreach (var change in log.Since(time))
            {
                if (change.Before is null)
                {
                    items.Remove(change.Key);
                }
                else
                {
                    items[change.Key] = change.Before;
                }
            }
        }

        return items;
    }

    private Dictionary<string, DynamoValue>? Remove(DynamoKey key)
    {
        if (!_items.Remove(key, out var old))
        {
            return null;
        }

        Keys.Remove(key);
        SizeBytes -= DynamoValueRules.ItemSize(old);
        foreach (var index in Indexes)
        {
            index.Remove(key, old);
        }

        return old;
    }

    /// <summary>Fills a new index from the items already in the table, as DynamoDB backfills a new global index.</summary>
    public void Backfill(DynamoIndex index)
    {
        index.Clear();
        foreach (var (key, item) in All())
        {
            index.Add(key, item);
        }
    }
}

/// <summary>Where a restored table came from: a backup, or another table at a point in time.</summary>
internal sealed record DynamoRestore(string? SourceBackupArn, string SourceTableArn, DateTime RestoreDateTime);

/// <summary>A table's item changes while point-in-time recovery is on: each key's item before the change, and when.</summary>
internal sealed class DynamoPointInTimeLog(TimeProvider timeProvider)
{
    private readonly List<(DateTime At, DynamoKey Key, Dictionary<string, DynamoValue>? Before)> _changes = [];

    public void Record(DynamoKey key, Dictionary<string, DynamoValue>? before) =>
        _changes.Add((timeProvider.GetUtcNow().UtcDateTime, key, before));

    /// <summary>The changes made after <paramref name="time"/>, latest first, to undo in that order.</summary>
    public IEnumerable<(DateTime At, DynamoKey Key, Dictionary<string, DynamoValue>? Before)> Since(DateTime time)
    {
        for (var i = _changes.Count - 1; i >= 0 && _changes[i].At > time; i--)
        {
            yield return _changes[i];
        }
    }

    /// <summary>Forgets the changes made before <paramref name="time"/>, which no restore can reach any more.</summary>
    public void Forget(DateTime time)
    {
        var count = 0;
        while (count < _changes.Count && _changes[count].At < time)
        {
            count++;
        }

        _changes.RemoveRange(0, count);
    }
}

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

    /// <summary>The vector indexes, in creation order.</summary>
    public List<DynamoVectorIndex> VectorIndexes { get; } = [];

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

    /// <summary>The table's resource policy (its stream has its own).</summary>
    public DynamoResourcePolicy ResourcePolicy { get; } = new();

    public bool PointInTimeRecoveryEnabled { get; set; }

    public int PointInTimeRecoveryPeriodInDays { get; set; } = 35;

    /// <summary>When point-in-time recovery was last turned on, to the second; nothing earlier can be restored.</summary>
    public DateTime? PointInTimeRecoveryEnabledAt { get; set; }

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

    public DynamoVectorIndex? FindVectorIndex(string name) =>
        VectorIndexes.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.Ordinal));

    /// <summary>Whether any index of the table, secondary or vector, has this name.</summary>
    public bool HasIndexNamed(string name) => FindIndex(name) is not null || FindVectorIndex(name) is not null;

    /// <summary>
    /// Every attribute the table's key schema or an index's key schema or search schema names:
    /// the attributes the table's <c>AttributeDefinitions</c> may hold.
    /// </summary>
    public HashSet<string> UsedAttributeNames() =>
        new(
            KeySchema.Attributes.Concat(Indexes.SelectMany(i => i.KeySchema.Attributes)).Select(a => a.Name)
                .Concat(VectorIndexes.SelectMany(v => v.SearchSchema).Select(e => e.Name)),
            StringComparer.Ordinal);

    /// <summary>
    /// An attribute's size as DynamoDB bills it: a vector a vector index holds counts its name and
    /// four bytes a dimension, however many digits its numbers have; any other attribute counts
    /// its name and value, as <see cref="DynamoValueRules.ItemSize"/> does.
    /// </summary>
    public long BilledSize(string name, DynamoValue value)
    {
        foreach (var index in VectorIndexes)
        {
            if (string.Equals(index.VectorAttribute, name, StringComparison.Ordinal) && DynamoVectorIndex.Read(value, index.Dimensions) is not null)
            {
                return Encoding.UTF8.GetByteCount(name) + (4L * index.Dimensions);
            }
        }

        return Encoding.UTF8.GetByteCount(name) + value.Size();
    }

    /// <summary>An item's size as DynamoDB bills reads and writes of it (see <see cref="BilledSize(string, DynamoValue)"/>).</summary>
    public long BilledSize(IReadOnlyDictionary<string, DynamoValue>? item)
    {
        if (item is null)
        {
            return 0;
        }

        if (VectorIndexes.Count == 0)
        {
            return DynamoValueRules.ItemSize(item);
        }

        var size = 0L;
        foreach (var attribute in item)
        {
            size += BilledSize(attribute.Key, attribute.Value);
        }

        return size;
    }

    /// <summary>The billed size of the entry <paramref name="index"/> holds for <paramref name="item"/>.</summary>
    public long VectorEntrySize(DynamoVectorIndex index, IReadOnlyDictionary<string, DynamoValue> item) =>
        BilledSize(index.Entry(KeySchema, item));

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

        foreach (var vectorIndex in VectorIndexes)
        {
            vectorIndex.Add(key, item, VectorEntrySize(vectorIndex, item));
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

        foreach (var vectorIndex in VectorIndexes)
        {
            vectorIndex.Remove(key, VectorEntrySize(vectorIndex, old));
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

    /// <summary>Fills a new vector index from the items already in the table; items without a vector it can take are left out.</summary>
    public void Backfill(DynamoVectorIndex index)
    {
        index.Clear();
        foreach (var (key, item) in All())
        {
            index.Add(key, item, VectorEntrySize(index, item));
        }
    }
}

/// <summary>A resource's policy and its revision id, both null when it has none.</summary>
internal sealed class DynamoResourcePolicy
{
    public string? Policy { get; private set; }

    public string? Revision { get; private set; }

    public void Set(string? policy, TimeProvider timeProvider)
    {
        Policy = policy;
        Revision = policy is null ? null : timeProvider.GetUtcNow().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
    }
}

/// <summary>Where a restored table came from: a backup, or another table at a point in time.</summary>
internal sealed record DynamoRestore(string? SourceBackupArn, string SourceTableArn, DateTime RestoreDateTime);

/// <summary>
/// A table's item changes while point-in-time recovery is on: each key's item before the change,
/// and when. Changes older than the recovery period are dropped as new ones arrive, since no
/// restore can reach them.
/// </summary>
internal sealed class DynamoPointInTimeLog(TimeProvider timeProvider, TimeSpan retention)
{
    private readonly LinkedList<(DateTime At, DynamoKey Key, Dictionary<string, DynamoValue>? Before)> _changes = new();

    /// <summary>How long a change stays restorable: the table's recovery period.</summary>
    public TimeSpan Retention { get; set; } = retention;

    public void Record(DynamoKey key, Dictionary<string, DynamoValue>? before)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        Forget(now - Retention);
        _changes.AddLast((now, key, before));
    }

    /// <summary>The changes made after <paramref name="time"/>, latest first, to undo in that order.</summary>
    public IEnumerable<(DateTime At, DynamoKey Key, Dictionary<string, DynamoValue>? Before)> Since(DateTime time)
    {
        for (var node = _changes.Last; node is not null && node.Value.At > time; node = node.Previous)
        {
            yield return node.Value;
        }
    }

    /// <summary>Forgets the changes made before <paramref name="time"/>, which no restore can reach any more.</summary>
    private void Forget(DateTime time)
    {
        while (_changes.First is { } first && first.Value.At < time)
        {
            _changes.RemoveFirst();
        }
    }
}

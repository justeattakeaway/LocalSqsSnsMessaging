namespace LocalSqsSnsMessaging.DynamoDb;

/// <summary>One attribute of a key schema: its name and declared scalar type.</summary>
internal sealed record DynamoKeyAttribute(string Name, DynamoType Type);

/// <summary>
/// A table's or an index's key schema: its partition (HASH) attributes and its sort (RANGE)
/// attributes, in schema order. A table, and a local index, has one of the first and at most one
/// of the second; a global index up to four of each (a multi-attribute key).
/// </summary>
/// <remarks>
/// A key of several attributes in one role is held as one composite value, a list of the
/// attributes' values in schema order (see <see cref="KeyOf"/>), so a <see cref="DynamoKey"/> has
/// a partition and a sort value either way, and keys order by their attributes in schema order
/// (<see cref="DynamoKeyOrder"/> compares lists element by element).
/// </remarks>
internal sealed class DynamoKeySchema
{
    public DynamoKeySchema(DynamoKeyAttribute partition, DynamoKeyAttribute? sort)
        : this([partition], sort is null ? [] : [sort])
    {
    }

    public DynamoKeySchema(IReadOnlyList<DynamoKeyAttribute> hash, IReadOnlyList<DynamoKeyAttribute> range)
    {
        Hash = hash;
        Range = range;
    }

    /// <summary>The partition (HASH) attributes, in schema order; at least one.</summary>
    public IReadOnlyList<DynamoKeyAttribute> Hash { get; }

    /// <summary>The sort (RANGE) attributes, in schema order; possibly none.</summary>
    public IReadOnlyList<DynamoKeyAttribute> Range { get; }

    /// <summary>The first partition attribute: a table's (or a single-attribute index's) partition key.</summary>
    public DynamoKeyAttribute Partition => Hash[0];

    /// <summary>The first sort attribute, if any: a table's (or a single-attribute index's) sort key.</summary>
    public DynamoKeyAttribute? Sort => Range.Count > 0 ? Range[0] : null;

    /// <summary>Whether a role has several attributes, so its key values are composite.</summary>
    public bool IsMultiAttribute => Hash.Count > 1 || Range.Count > 1;

    public IEnumerable<DynamoKeyAttribute> Attributes => Hash.Concat(Range);

    public bool IsKeyAttribute(string name) =>
        Attributes.Any(a => string.Equals(a.Name, name, StringComparison.Ordinal));

    /// <summary>The key of <paramref name="item"/> under this schema, or null when it lacks a key attribute or has one of the wrong type.</summary>
    public DynamoKey? KeyOf(IReadOnlyDictionary<string, DynamoValue> item)
    {
        if (Value(Hash, item) is not { } partition)
        {
            return null;
        }

        DynamoValue? sort = null;
        if (Range.Count > 0 && (sort = Value(Range, item)) is null)
        {
            return null;
        }

        return new DynamoKey(partition, sort);
    }

    /// <summary>The value of one role's attributes in <paramref name="item"/>: the value itself, or (for several) a list of them.</summary>
    private static DynamoValue? Value(IReadOnlyList<DynamoKeyAttribute> attributes, IReadOnlyDictionary<string, DynamoValue> item)
    {
        if (attributes.Count == 1)
        {
            return item.TryGetValue(attributes[0].Name, out var single) && single.Type == attributes[0].Type ? single : null;
        }

        var values = new DynamoValue[attributes.Count];
        for (var i = 0; i < attributes.Count; i++)
        {
            if (!item.TryGetValue(attributes[i].Name, out var value) || value.Type != attributes[i].Type)
            {
                return null;
            }

            values[i] = value;
        }

        return DynamoValue.FromList(values);
    }

    /// <summary>The key attributes of <paramref name="key"/> as an item fragment.</summary>
    public Dictionary<string, DynamoValue> ToAttributes(DynamoKey key)
    {
        var attributes = new Dictionary<string, DynamoValue>(StringComparer.Ordinal);
        Spread(Hash, key.Partition, attributes);
        if (Range.Count > 0 && key.Sort is not null)
        {
            Spread(Range, key.Sort, attributes);
        }

        return attributes;
    }

    private static void Spread(IReadOnlyList<DynamoKeyAttribute> attributes, DynamoValue value, Dictionary<string, DynamoValue> into)
    {
        if (attributes.Count == 1)
        {
            into[attributes[0].Name] = value;
            return;
        }

        for (var i = 0; i < attributes.Count; i++)
        {
            into[attributes[i].Name] = value.L[i];
        }
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

    /// <summary>
    /// <c>CREATING</c>, <c>ACTIVE</c> or (a global index UpdateTable deletes) <c>DELETING</c>. An
    /// index created with its table is <c>CREATING</c> while the table is.
    /// </summary>
    public string Status { get; private set; } = "ACTIVE";

    /// <summary>While an index UpdateTable adds is <c>CREATING</c>, when it becomes <c>ACTIVE</c>.</summary>
    public DateTimeOffset? ActiveAt { get; private set; }

    /// <summary>While <c>DELETING</c>, when the index is gone.</summary>
    public DateTimeOffset? GoneAt { get; private set; }

    /// <summary>Starts the index <c>CREATING</c>: until <paramref name="activeAt"/>, or with its table when null.</summary>
    public void StartCreating(DateTimeOffset? activeAt)
    {
        Status = "CREATING";
        ActiveAt = activeAt;
    }

    /// <summary>Starts the index <c>DELETING</c>, until <paramref name="goneAt"/>.</summary>
    public void StartDeleting(DateTimeOffset goneAt)
    {
        Status = "DELETING";
        ActiveAt = null;
        GoneAt = goneAt;
    }

    /// <summary>
    /// Moves the index on as its time comes (<paramref name="tableActive"/>: the table has finished
    /// creating, for an index created with it); false once a deleted index is gone.
    /// </summary>
    public bool Settle(DateTimeOffset now, bool tableActive)
    {
        if (GoneAt is { } goneAt)
        {
            return now < goneAt;
        }

        if (Status == "CREATING" && (ActiveAt is { } activeAt ? now >= activeAt : tableActive))
        {
            Status = "ACTIVE";
            ActiveAt = null;
        }

        return true;
    }

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

    // Lifecycle: DynamoDB's asynchronous changes take a while (the bus's DynamoDbTransitionDelay),
    // and the table passes through a transient status meanwhile. Each change stores when it is
    // done; Settle moves the table on once that time has come, so nothing waits on a timer.

    /// <summary><c>CREATING</c>, <c>UPDATING</c>, <c>DELETING</c> or <c>ACTIVE</c>, as of the last <see cref="Settle"/>.</summary>
    public string Status { get; private set; } = "ACTIVE";

    /// <summary>While <c>CREATING</c>, when the table (and the indexes created with it) becomes <c>ACTIVE</c>.</summary>
    public DateTimeOffset? ActiveAt { get; private set; }

    /// <summary>While a throughput or billing-mode change is being applied (the table <c>UPDATING</c>), when it is done.</summary>
    public DateTimeOffset? ThroughputSettlesAt { get; private set; }

    /// <summary>While <c>DELETING</c>, when the table is gone.</summary>
    public DateTimeOffset? GoneAt { get; private set; }

    /// <summary>Whether a global or vector index is being created or deleted, which keeps the table <c>UPDATING</c>.</summary>
    public bool IndexInTransition =>
        GlobalIndexes.Any(i => i.Status != "ACTIVE") || VectorIndexes.Any(v => v.Status != "ACTIVE");

    /// <summary>Starts the table, and its global and vector indexes, <c>CREATING</c> until <paramref name="activeAt"/>.</summary>
    public void StartCreating(DateTimeOffset activeAt)
    {
        ActiveAt = activeAt;
        foreach (var index in GlobalIndexes)
        {
            index.StartCreating(null);
        }

        foreach (var vectorIndex in VectorIndexes)
        {
            vectorIndex.Status = "CREATING";
        }

        Status = "CREATING";
    }

    /// <summary>Starts a throughput or billing-mode change, done at <paramref name="settlesAt"/>.</summary>
    public void StartThroughputChange(DateTimeOffset settlesAt)
    {
        ThroughputSettlesAt = settlesAt;
        Status = "UPDATING";
    }

    /// <summary>Starts the table <c>DELETING</c>, until <paramref name="goneAt"/>.</summary>
    public void StartDeleting(DateTimeOffset goneAt)
    {
        GoneAt = goneAt;
        Status = "DELETING";
    }

    /// <summary>
    /// Moves the table, its indexes and its contributor insights on through every change whose
    /// time has come; false once a deleted table is gone.
    /// </summary>
    public bool Settle(DateTimeOffset now)
    {
        if (GoneAt is { } goneAt)
        {
            return now < goneAt;
        }

        if (ActiveAt is { } activeAt && now >= activeAt)
        {
            ActiveAt = null;
            foreach (var vectorIndex in VectorIndexes.Where(v => v.ActiveAt is null))
            {
                vectorIndex.Status = "ACTIVE";
            }
        }

        var removed = GlobalIndexes.RemoveAll(index => !index.Settle(now, tableActive: ActiveAt is null));
        if (removed > 0)
        {
            var stillUsed = UsedAttributeNames();
            AttributeDefinitions.RemoveAll(a => !stillUsed.Contains(a.Name));
        }

        foreach (var vectorIndex in VectorIndexes)
        {
            vectorIndex.Refresh(now);
        }

        if (ThroughputSettlesAt is { } settlesAt && now >= settlesAt)
        {
            ThroughputSettlesAt = null;
        }

        foreach (var insights in ContributorInsights.Values)
        {
            insights.Settle(now);
        }

        Status = ActiveAt is not null ? "CREATING"
            : ThroughputSettlesAt is not null || IndexInTransition ? "UPDATING"
            : "ACTIVE";
        return true;
    }

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
            if (string.Equals(index.VectorAttribute, name, StringComparison.Ordinal) && DynamoVectorIndex.IsShaped(value, index.Dimensions))
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
            AddToVectorIndex(vectorIndex, key, item);
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
            vectorIndex.Remove(key);
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
            AddToVectorIndex(index, key, item);
        }
    }

    /// <summary>Indexes the item's vector, if it holds one the index can take.</summary>
    private void AddToVectorIndex(DynamoVectorIndex index, DynamoKey key, Dictionary<string, DynamoValue> item)
    {
        if (index.IndexableVector(item) is { } vector)
        {
            index.Add(key, vector, VectorEntrySize(index, item));
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

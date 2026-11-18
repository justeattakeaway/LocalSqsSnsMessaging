namespace InMemoryAws.DynamoDb;

/// <summary>
/// A global table (version 2019.11.21): same-named tables in several regions of the cloud, each a
/// replica of the others, and (for multi-Region strong consistency) witness regions that hold no
/// table. Every regional table refers to its group (<see cref="DynamoTable.Replication"/>).
/// </summary>
/// <remarks>
/// <para>
/// A write to one replica is applied to every other replica as it happens (<see cref="Replicate"/>),
/// so replication has no lag: a read in another region sees it at once, which is what multi-Region
/// strong consistency promises and eventual consistency allows.
/// </para>
/// <para>
/// A replica's status is its regional table's (<c>CREATING</c> while UpdateTable's
/// <c>ReplicaUpdates</c> builds it, <c>DELETING</c> while it is removed). <see cref="Settle"/> moves
/// every member on, removes a replica once its table is gone, and dissolves the group when one
/// table and no witness is left. Guarded by the cloud's DynamoDB lock, which every region shares.
/// </para>
/// </remarks>
internal sealed class DynamoReplicationGroup
{
    /// <summary>How long a region that a replica was added from can't delete its table.</summary>
    public static readonly TimeSpan SourceRetention = TimeSpan.FromHours(24);

    private readonly List<DynamoReplicaMember> _members = [];
    private readonly List<DynamoWitness> _witnesses = [];

    /// <summary>The regional tables, in the order they joined.</summary>
    public IReadOnlyList<DynamoReplicaMember> Members => _members;

    /// <summary>The witness regions (multi-Region strong consistency), in the order they joined.</summary>
    public IReadOnlyList<DynamoWitness> Witnesses => _witnesses;

    /// <summary><c>EVENTUAL</c> or <c>STRONG</c>.</summary>
    public string MultiRegionConsistency { get; set; } = "EVENTUAL";

    /// <summary>Whether a replica or witness is being created or deleted.</summary>
    public bool InTransition =>
        _members.Any(m => m.Table.Status is "CREATING" or "DELETING") || _witnesses.Any(w => w.Status != "ACTIVE");

    public DynamoReplicaMember? Member(string region) =>
        _members.FirstOrDefault(m => string.Equals(m.Store.Region, region, StringComparison.Ordinal));

    public DynamoReplicaMember? Member(DynamoTable table) =>
        _members.FirstOrDefault(m => ReferenceEquals(m.Table, table));

    public DynamoWitness? Witness(string region) =>
        _witnesses.FirstOrDefault(w => string.Equals(w.Region, region, StringComparison.Ordinal));

    /// <summary>Adds a regional table to the group.</summary>
    public DynamoReplicaMember Join(DynamoDbStore store, DynamoTable table)
    {
        var member = new DynamoReplicaMember(store, table);
        _members.Add(member);
        table.Replication = this;
        return member;
    }

    /// <summary>Adds a witness region, <c>CREATING</c> until <paramref name="activeAt"/> (or <c>ACTIVE</c> at once when null).</summary>
    public void AddWitness(string region, DateTimeOffset? activeAt) => _witnesses.Add(new DynamoWitness(region, activeAt));

    /// <summary>Takes a gone table out of the group, and dissolves the group if nothing is left to replicate to.</summary>
    public void Leave(DynamoTable table)
    {
        _members.RemoveAll(m => ReferenceEquals(m.Table, table));
        if (ReferenceEquals(table.Replication, this))
        {
            table.Replication = null;
        }

        DissolveIfAlone();
    }

    /// <summary>Takes a gone witness out of the group, and dissolves the group if nothing is left to replicate to.</summary>
    public void RemoveWitness(DynamoWitness witness)
    {
        _witnesses.Remove(witness);
        DissolveIfAlone();
    }

    /// <summary>
    /// Moves every member and witness on through the changes whose time has come, removing a
    /// replica whose table is gone (from its region too) and a witness that is gone.
    /// </summary>
    public void Settle(DateTimeOffset now)
    {
        foreach (var member in _members.ToList())
        {
            if (!member.Table.Settle(now))
            {
                if (member.Store.Tables.TryGetValue(member.Table.Name, out var held) && ReferenceEquals(held, member.Table))
                {
                    member.Store.Tables.Remove(member.Table.Name);
                }

                Leave(member.Table);
            }
        }

        foreach (var witness in _witnesses.ToList())
        {
            if (!witness.Settle(now))
            {
                RemoveWitness(witness);
            }
        }

        // Each table's status depends on the others' (a replica being built leaves them UPDATING).
        foreach (var member in _members)
        {
            member.Table.Settle(now);
        }
    }

    /// <summary>Applies a write to one replica to every other replica, and to their streams.</summary>
    public void Replicate(DynamoTable origin, DynamoItemChange change, DateTimeOffset now)
    {
        foreach (var member in _members)
        {
            if (ReferenceEquals(member.Table, origin))
            {
                continue;
            }

            var table = member.Table;
            var applied = change.NewImage is { } image
                ? table.Put(change.Key, image.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal))
                : table.Delete(change.Key, change.ByTimeToLive);
            if (table.StreamEnabled && table.LatestStream is { Enabled: true } stream)
            {
                stream.Append(applied, table, now);
            }
        }
    }

    private void DissolveIfAlone()
    {
        if (_members.Count == 1 && _witnesses.Count == 0)
        {
            _members[0].Table.Replication = null;
            _members.Clear();
        }
    }
}

/// <summary>One regional table of a global table.</summary>
internal sealed class DynamoReplicaMember(DynamoDbStore store, DynamoTable table)
{
    /// <summary>The region's store, which holds the table.</summary>
    public DynamoDbStore Store { get; } = store;

    public DynamoTable Table { get; } = table;

    /// <summary>
    /// Until when the table can't be deleted, having been the source a replica was added from in
    /// the last 24 hours.
    /// </summary>
    public DateTimeOffset? SourceUntil { get; set; }
}

/// <summary>A witness region of a multi-Region strongly consistent global table: it holds no table.</summary>
internal sealed class DynamoWitness(string region, DateTimeOffset? activeAt)
{
    private DateTimeOffset? _activeAt = activeAt;
    private DateTimeOffset? _goneAt;

    public string Region { get; } = region;

    /// <summary><c>CREATING</c>, <c>ACTIVE</c> or <c>DELETING</c>.</summary>
    public string Status { get; private set; } = activeAt is null ? "ACTIVE" : "CREATING";

    public void StartDeleting(DateTimeOffset goneAt)
    {
        Status = "DELETING";
        _activeAt = null;
        _goneAt = goneAt;
    }

    /// <summary>Moves the witness on as its time comes; false once a deleted witness is gone.</summary>
    public bool Settle(DateTimeOffset now)
    {
        if (_goneAt is { } goneAt)
        {
            return now < goneAt;
        }

        if (_activeAt is { } activeAt && now >= activeAt)
        {
            _activeAt = null;
            Status = "ACTIVE";
        }

        return true;
    }
}

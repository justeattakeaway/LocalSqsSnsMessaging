using LocalSqsSnsMessaging.DynamoDb.Model;

#pragma warning disable CA1822 // The generated dispatcher calls every operation on a client instance.

namespace LocalSqsSnsMessaging.DynamoDb;

/// <summary>
/// Global tables. Version 2019.11.21's are made by UpdateTable's <c>ReplicaUpdates</c> (and, for
/// multi-Region strong consistency, <c>GlobalTableWitnessUpdates</c> and
/// <c>MultiRegionConsistency</c>): a replica is a same-named table in another region of the bus,
/// kept in step by <see cref="DynamoReplicationGroup"/>. The operations of version 2017.11.29
/// (CreateGlobalTable, UpdateGlobalTable, DescribeGlobalTable, the global table settings) answer as
/// DynamoDB answers them now that the version is withdrawn: creating one, or adding a replica to
/// one, is refused, and since no such table can exist (they don't see 2019.11.21 tables), every
/// other call finds none. Replica auto scaling is not emulated, so no table has any.
/// </summary>
internal sealed partial class InternalDynamoDbClient
{
    private const string LegacyGlobalTablesVersion =
        "One or more parameter values were invalid: DynamoDB global tables version 2017.11.29 is not supported. "
        + "We recommend using DynamoDB global tables version 2019.11.21, instead of version 2017.11.29 (Legacy).";

    public Task<CreateGlobalTableResponse> CreateGlobalTableAsync(CreateGlobalTableRequest request, CancellationToken cancellationToken = default) =>
        throw DynamoDbErrors.Validation(LegacyGlobalTablesVersion);

    public Task<UpdateGlobalTableResponse> UpdateGlobalTableAsync(UpdateGlobalTableRequest request, CancellationToken cancellationToken = default) =>
        request.ReplicaUpdates?.Any(update => update.Create is not null) == true
            ? throw DynamoDbErrors.Validation(LegacyGlobalTablesVersion)
            : throw GlobalTableNotFound(request.GlobalTableName!);

    public Task<DescribeGlobalTableResponse> DescribeGlobalTableAsync(DescribeGlobalTableRequest request, CancellationToken cancellationToken = default) =>
        throw GlobalTableNotFound(request.GlobalTableName!);

    public Task<ListGlobalTablesResponse> ListGlobalTablesAsync(ListGlobalTablesRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ListGlobalTablesResponse { GlobalTables = [] });

    public Task<DescribeGlobalTableSettingsResponse> DescribeGlobalTableSettingsAsync(DescribeGlobalTableSettingsRequest request, CancellationToken cancellationToken = default) =>
        throw GlobalTableSettingsNotFound(request.GlobalTableName!);

    public Task<UpdateGlobalTableSettingsResponse> UpdateGlobalTableSettingsAsync(UpdateGlobalTableSettingsRequest request, CancellationToken cancellationToken = default) =>
        throw GlobalTableSettingsNotFound(request.GlobalTableName!);

    public Task<DescribeTableReplicaAutoScalingResponse> DescribeTableReplicaAutoScalingAsync(DescribeTableReplicaAutoScalingRequest request, CancellationToken cancellationToken = default)
    {
        lock (Store.Gate)
        {
            if (FindTable(TableName(request.TableName!)) is { } table)
            {
                Authorize(table, "DescribeTableReplicaAutoScaling");
            }

            throw NotAGlobalTable(request.TableName!);
        }
    }

    public Task<UpdateTableReplicaAutoScalingResponse> UpdateTableReplicaAutoScalingAsync(UpdateTableReplicaAutoScalingRequest request, CancellationToken cancellationToken = default) =>
        throw NotAGlobalTable(request.TableName!);

    /// <summary>DescribeGlobalTable's and UpdateGlobalTable's refusal of a name no global table has.</summary>
    private static DynamoDbException GlobalTableNotFound(string name) =>
        new("GlobalTableNotFoundException", $"Global table not found: Global table with name: '{name}' does not exist.");

    /// <summary>The global table settings operations' refusal of a name no global table has: the same, unprefixed.</summary>
    private static DynamoDbException GlobalTableSettingsNotFound(string name) =>
        new("GlobalTableNotFoundException", $"Global table with name: '{name}' does not exist.");

    /// <summary>Replica auto scaling's refusal of a table with no replicas, or no table at all.</summary>
    private static DynamoDbException NotAGlobalTable(string name) =>
        DynamoDbErrors.ResourceNotFound($"Global table with name: '{name}' does not exist.");

    private const string LocalRegionReplica = "Cannot add or delete the local region through ReplicaUpdates. Use CreateTable, DeleteTable, or UpdateTable as required.";

    private const string ReplicaNotFound = "Replica specified in the Replica Update or Replica Delete action of the request was not found.";

    /// <summary>
    /// Checks UpdateTable's <c>ReplicaUpdates</c>, <c>GlobalTableWitnessUpdates</c> and
    /// <c>MultiRegionConsistency</c> against the table as it stands, and returns the change to
    /// make (null when the request has none of them).
    /// </summary>
    private Action? PrepareReplicaUpdates(DynamoTable table, UpdateTableRequest request)
    {
        var replicaUpdates = request.ReplicaUpdates ?? [];
        var witnessUpdates = request.GlobalTableWitnessUpdates ?? [];
        if (replicaUpdates.Count == 0 && witnessUpdates.Count == 0 && request.MultiRegionConsistency is null)
        {
            return null;
        }

        static string? RegionOf(ReplicationGroupUpdate update) =>
            update.Create?.RegionName ?? update.Update?.RegionName ?? update.Delete?.RegionName;

        if (replicaUpdates.Any(u => string.Equals(RegionOf(u), Region, StringComparison.Ordinal)))
        {
            throw DynamoDbErrors.Validation(LocalRegionReplica);
        }

        var group = table.Replication;
        var creates = replicaUpdates.Where(u => u.Create is not null).Select(u => u.Create!.RegionName!).ToList();
        var deletes = replicaUpdates.Where(u => u.Delete is not null).Select(u => u.Delete!.RegionName!).ToList();
        var witnessCreates = witnessUpdates.Where(u => u.Create is not null).Select(u => u.Create!.RegionName!).ToList();
        var witnessDeletes = witnessUpdates.Where(u => u.Delete is not null).Select(u => u.Delete!.RegionName!).ToList();

        // Multi-Region strong consistency spans exactly three regions: this one and two replicas,
        // or one replica and one witness.
        if (request.MultiRegionConsistency == "STRONG")
        {
            var replicas = (group?.Members.Count(m => !ReferenceEquals(m.Table, table)) ?? 0) + creates.Count - deletes.Count;
            var witnesses = (group?.Witnesses.Count ?? 0) + witnessCreates.Count - witnessDeletes.Count;
            if (!((replicas == 2 && witnesses == 0) || (replicas == 1 && witnesses == 1)))
            {
                throw DynamoDbErrors.Validation("Unsupported replica count for global tables with MultiRegionConsistency set to STRONG.");
            }

            if (table.LocalIndexes.Count > 0)
            {
                throw DynamoDbErrors.Validation("Local secondary indexes are not supported for global tables with MultiRegionConsistency set to STRONG.");
            }
        }

        // A replica or witness still being created or deleted holds up any other change to the group.
        if (group?.InTransition == true)
        {
            throw DynamoDbErrors.ResourceInUse("The resource which you are attempting to change is in use.");
        }

        foreach (var region in deletes.Concat(replicaUpdates.Where(u => u.Update is not null).Select(u => u.Update!.RegionName!)))
        {
            if (group?.Member(region) is null)
            {
                throw DynamoDbErrors.Validation(ReplicaNotFound);
            }
        }

        foreach (var region in witnessDeletes)
        {
            if (group?.Witness(region) is null)
            {
                throw DynamoDbErrors.Validation(ReplicaNotFound);
            }
        }

        // A region that already has a table of this name, replica or not, can't take a new replica.
        foreach (var region in creates)
        {
            if (group?.Member(region) is not null
                || (_bus.TryGetRegion(region, out var resources) && resources.DynamoDb.Tables.ContainsKey(table.Name)))
            {
                throw DynamoDbErrors.Validation($"Failed to create a the new replica of table with name: \u2018{table.Name}\u2019 because one or more replicas already existed as tables.");
            }
        }

        return () =>
        {
            var now = _bus.TimeProvider.GetUtcNow();
            DateTimeOffset? activeAt = TransitionsTakeTime ? TransitionEnd : null;
            if (table.Replication is null)
            {
                group = new DynamoReplicationGroup();
                group.Join(Store, table);
            }

            group = table.Replication!;
            if (request.MultiRegionConsistency is { } consistency)
            {
                group.MultiRegionConsistency = consistency;
            }

            foreach (var region in creates)
            {
                var store = _bus.Region(region).DynamoDb;
                var replica = Copy(table, table.Name, $"arn:aws:dynamodb:{region}:{_bus.CurrentAccountId}:table/{table.Name}", Guid.NewGuid().ToString(), Now);
                replica.StreamEnabled = table.StreamEnabled;
                replica.StreamViewType = table.StreamViewType;
                replica.DeletionProtectionEnabled = false;
                if (activeAt is { } replicaActiveAt)
                {
                    replica.StartCreating(replicaActiveAt);
                }

                if (replica.StreamEnabled)
                {
                    StartStream(replica, store);
                }

                store.Tables[table.Name] = replica;
                group.Join(store, replica);
                group.Member(table)!.SourceUntil = now + DynamoReplicationGroup.SourceRetention;
            }

            foreach (var region in deletes)
            {
                var member = group.Member(region)!;
                StartDeleting(member.Store, member.Table);
            }

            foreach (var region in witnessCreates)
            {
                group.AddWitness(region, activeAt);
            }

            foreach (var region in witnessDeletes)
            {
                var witness = group.Witness(region)!;
                witness.StartDeleting(now + _bus.DynamoDbTransitionDelay);
                if (!TransitionsTakeTime)
                {
                    group.RemoveWitness(witness);
                }
            }
        };
    }
}

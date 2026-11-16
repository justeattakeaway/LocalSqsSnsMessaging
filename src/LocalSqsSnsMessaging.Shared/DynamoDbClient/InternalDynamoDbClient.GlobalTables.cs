using LocalSqsSnsMessaging.DynamoDb.Model;

#pragma warning disable CA1822 // The generated dispatcher calls every operation on a client instance.

namespace LocalSqsSnsMessaging.DynamoDb;

/// <summary>
/// Global tables. The operations of global tables version 2017.11.29 (CreateGlobalTable,
/// UpdateGlobalTable, DescribeGlobalTable, the global table settings) answer as DynamoDB answers
/// them now that the version is withdrawn: creating one, or adding a replica to one, is refused,
/// and since no such table can exist, every other call finds none. Replica auto scaling belongs to
/// global tables too, so a table without replicas has none.
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
            if (Store.Tables.TryGetValue(TableName(request.TableName!), out var table))
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
}

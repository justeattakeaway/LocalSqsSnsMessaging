using System.Text;
using LocalSqsSnsMessaging.DynamoDb.Model;

#pragma warning disable CA1822 // The generated dispatcher calls every operation on a client instance.

namespace LocalSqsSnsMessaging.DynamoDb;

/// <summary>
/// Backups and the table settings that need no other service: on-demand backups and restores,
/// point-in-time recovery settings, resource policies, contributor insights, and the empty
/// listings of global tables, exports, imports and Kinesis destinations.
/// </summary>
internal sealed partial class InternalDynamoDbClient
{
    private DynamoTable TableForBackup(string? tableName)
    {
        var name = TableName(tableName!);
        return Store.Tables.TryGetValue(name, out var table)
            ? table
            : throw new DynamoDbException("TableNotFoundException", $"Table not found: {name}");
    }

    public Task<DescribeContinuousBackupsResponse> DescribeContinuousBackupsAsync(DescribeContinuousBackupsRequest request, CancellationToken cancellationToken = default)
    {
        lock (Store.Gate)
        {
            var table = TableForBackup(request.TableName);
            Record("DescribeContinuousBackups", table.Arn);
            return Task.FromResult(new DescribeContinuousBackupsResponse { ContinuousBackupsDescription = ContinuousBackups(table) });
        }
    }

    private ContinuousBackupsDescription ContinuousBackups(DynamoTable table) => new()
    {
        ContinuousBackupsStatus = "ENABLED",
        PointInTimeRecoveryDescription = table.PointInTimeRecoveryEnabled
            ? new PointInTimeRecoveryDescription
            {
                PointInTimeRecoveryStatus = "ENABLED",
                RecoveryPeriodInDays = table.PointInTimeRecoveryPeriodInDays,
                EarliestRestorableDateTime = table.EarliestRestorableDateTime,
                LatestRestorableDateTime = LatestRestorableDateTime(table),
            }
            : new PointInTimeRecoveryDescription { PointInTimeRecoveryStatus = "DISABLED" },
    };

    public Task<UpdateContinuousBackupsResponse> UpdateContinuousBackupsAsync(UpdateContinuousBackupsRequest request, CancellationToken cancellationToken = default)
    {
        lock (Store.Gate)
        {
            var table = TableForBackup(request.TableName);
            var specification = request.PointInTimeRecoverySpecification!;
            if (specification.RecoveryPeriodInDays is { } days)
            {
                if (days is < 1 or > 35)
                {
                    throw DynamoDbErrors.Validation("RecoveryPeriodInDays must be between 1 and 35 days");
                }

                if (specification.PointInTimeRecoveryEnabled != true)
                {
                    throw DynamoDbErrors.Validation("RecoveryPeriodInDays can only be specified when PointInTimeRecoveryEnabled is true");
                }
            }

            var enable = specification.PointInTimeRecoveryEnabled ?? false;
            if (enable && !table.PointInTimeRecoveryEnabled)
            {
                // To the second, as DynamoDB reports it, so it reads back exactly as a RestoreDateTime.
                var now = Now;
                table.EarliestRestorableDateTime = new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
                table.PointInTimeLog = new DynamoPointInTimeLog(_bus.TimeProvider);
            }
            else if (!enable)
            {
                table.EarliestRestorableDateTime = null;
                table.PointInTimeLog = null;
            }

            table.PointInTimeRecoveryEnabled = enable;
            table.PointInTimeRecoveryPeriodInDays = specification.RecoveryPeriodInDays ?? (enable ? table.PointInTimeRecoveryPeriodInDays : 35);
            Record("UpdateContinuousBackups", table.Arn);
            return Task.FromResult(new UpdateContinuousBackupsResponse { ContinuousBackupsDescription = ContinuousBackups(table) });
        }
    }

    public Task<CreateBackupResponse> CreateBackupAsync(CreateBackupRequest request, CancellationToken cancellationToken = default)
    {
        lock (Store.Gate)
        {
            var table = TableForBackup(request.TableName);
            var created = Now;
            var arn = $"{table.Arn}/backup/{created.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture)}-{Guid.NewGuid().ToString("N")[..8]}";
            var backup = new DynamoBackup
            {
                Arn = arn,
                Name = request.BackupName!,
                CreationDateTime = created,
                Snapshot = Copy(table, table.Name, table.Arn, table.TableId),
                TableName = table.Name,
                TableArn = table.Arn,
                TableId = table.TableId,
                SizeBytes = table.SizeBytes,
            };
            Store.Backups[arn] = backup;
            Record("CreateBackup", table.Arn);
            return Task.FromResult(new CreateBackupResponse { BackupDetails = Details(backup) });
        }
    }

    private static BackupDetails Details(DynamoBackup backup) => new()
    {
        BackupArn = backup.Arn,
        BackupName = backup.Name,
        BackupSizeBytes = backup.SizeBytes,
        BackupStatus = backup.Status,
        BackupType = "USER",
        BackupCreationDateTime = backup.CreationDateTime,
    };

    private DynamoBackup FindBackup(string? arn) =>
        arn is not null && Store.Backups.TryGetValue(arn, out var backup)
            ? backup
            : throw new DynamoDbException("BackupNotFoundException", $"Backup not found: {arn}");

    public Task<DescribeBackupResponse> DescribeBackupAsync(DescribeBackupRequest request, CancellationToken cancellationToken = default)
    {
        lock (Store.Gate)
        {
            var backup = FindBackup(request.BackupArn);
            var snapshot = backup.Snapshot;
            Record("DescribeBackup", backup.Arn);
            return Task.FromResult(new DescribeBackupResponse
            {
                BackupDescription = new BackupDescription
                {
                    BackupDetails = Details(backup),
                    SourceTableDetails = new SourceTableDetails
                    {
                        TableName = backup.TableName,
                        TableId = backup.TableId,
                        TableArn = backup.TableArn,
                        TableSizeBytes = snapshot.SizeBytes,
                        KeySchema = KeySchemaElements(snapshot.KeySchema),
                        TableCreationDateTime = snapshot.CreationDateTime,
                        ProvisionedThroughput = new ProvisionedThroughput { ReadCapacityUnits = snapshot.Throughput.ReadCapacityUnits, WriteCapacityUnits = snapshot.Throughput.WriteCapacityUnits },
                        ItemCount = snapshot.ItemCount,
                        BillingMode = snapshot.BillingMode,
                    },
                },
            });
        }
    }

    public Task<ListBackupsResponse> ListBackupsAsync(ListBackupsRequest request, CancellationToken cancellationToken = default)
    {
        lock (Store.Gate)
        {
            var backups = Store.Backups.Values
                .Where(b => request.TableName is null || string.Equals(b.TableName, TableName(request.TableName), StringComparison.Ordinal))
                .Where(b => request.TimeRangeLowerBound is null || b.CreationDateTime >= request.TimeRangeLowerBound)
                .Where(b => request.TimeRangeUpperBound is null || b.CreationDateTime <= request.TimeRangeUpperBound)
                .Where(b => request.BackupType is null or "USER" or "ALL")
                .OrderBy(b => b.CreationDateTime)
                .ThenBy(b => b.Arn, StringComparer.Ordinal)
                .ToList();
            if (request.ExclusiveStartBackupArn is { } start)
            {
                var at = backups.FindIndex(b => string.Equals(b.Arn, start, StringComparison.Ordinal));
                backups = at >= 0 ? backups.Skip(at + 1).ToList() : backups;
            }

            var limit = request.Limit ?? 100;
            var page = backups.Take(limit).ToList();
            Record("ListBackups");
            return Task.FromResult(new ListBackupsResponse
            {
                BackupSummaries = page.Select(b => new BackupSummary
                {
                    TableName = b.TableName,
                    TableId = b.TableId,
                    TableArn = b.TableArn,
                    BackupArn = b.Arn,
                    BackupName = b.Name,
                    BackupCreationDateTime = b.CreationDateTime,
                    BackupStatus = b.Status,
                    BackupType = "USER",
                    BackupSizeBytes = b.SizeBytes,
                }).ToList(),
                LastEvaluatedBackupArn = backups.Count > limit ? page[^1].Arn : null,
            });
        }
    }

    public Task<DeleteBackupResponse> DeleteBackupAsync(DeleteBackupRequest request, CancellationToken cancellationToken = default)
    {
        lock (Store.Gate)
        {
            var backup = FindBackup(request.BackupArn);
            Store.Backups.Remove(backup.Arn);
            backup.Status = "DELETED";
            Record("DeleteBackup", backup.Arn);
            return Task.FromResult(new DeleteBackupResponse
            {
                BackupDescription = new BackupDescription { BackupDetails = Details(backup) },
            });
        }
    }

    public Task<RestoreTableFromBackupResponse> RestoreTableFromBackupAsync(RestoreTableFromBackupRequest request, CancellationToken cancellationToken = default)
    {
        var name = request.TargetTableName!;
        CheckTableName(name, "targetTableName");
        lock (Store.Gate)
        {
            var backup = FindBackup(request.BackupArn);
            if (Store.Tables.ContainsKey(name))
            {
                throw new DynamoDbException("TableAlreadyExistsException", $"Table already exists: {name}");
            }

            var table = Copy(backup.Snapshot, name, TableArn(name), Guid.NewGuid().ToString());
            if (request.BillingModeOverride is { } billing)
            {
                table.BillingMode = billing;
                table.BillingModeStated = true;
            }

            if (request.ProvisionedThroughputOverride is { } throughput)
            {
                table.Throughput = new DynamoThroughput { ReadCapacityUnits = throughput.ReadCapacityUnits ?? 0, WriteCapacityUnits = throughput.WriteCapacityUnits ?? 0 };
            }

            table.Restore = new DynamoRestore(backup.Arn, backup.TableArn, Now);
            Store.Tables[name] = table;
            Record("RestoreTableFromBackup", table.Arn);
            return Task.FromResult(new RestoreTableFromBackupResponse { TableDescription = Describe(table) });
        }
    }

    /// <summary>The latest time a table can be restored to: five minutes ago, as DynamoDB reports it, but never before the earliest.</summary>
    private DateTime LatestRestorableDateTime(DynamoTable table) =>
        Now.AddMinutes(-5) < table.EarliestRestorableDateTime ? table.EarliestRestorableDateTime.Value : Now.AddMinutes(-5);

    public Task<RestoreTableToPointInTimeResponse> RestoreTableToPointInTimeAsync(RestoreTableToPointInTimeRequest request, CancellationToken cancellationToken = default)
    {
        var name = request.TargetTableName!;
        CheckTableName(name, "targetTableName");
        if (request.SourceTableName is null && request.SourceTableArn is null)
        {
            throw DynamoDbErrors.InvalidParameter("either SourceTableName or SourceTableArn must be specified");
        }

        var useLatest = request.UseLatestRestorableTime ?? false;
        if (useLatest && request.RestoreDateTime is not null)
        {
            throw DynamoDbErrors.InvalidParameter("RestoreDateTime cannot be specified when UseLatestRestorableTime is true");
        }

        if (!useLatest && request.RestoreDateTime is null)
        {
            throw DynamoDbErrors.InvalidParameter("either RestoreDateTime or UseLatestRestorableTime must be specified");
        }

        lock (Store.Gate)
        {
            var source = TableForBackup(request.SourceTableName ?? request.SourceTableArn);
            if (!source.PointInTimeRecoveryEnabled || source.EarliestRestorableDateTime is not { } earliest)
            {
                throw new DynamoDbException("PointInTimeRecoveryUnavailableException", $"Point in time recovery is not enabled for table '{source.Name}'");
            }

            // Changes older than the recovery period are out of any restore's reach.
            var latest = LatestRestorableDateTime(source);
            source.PointInTimeLog?.Forget(Now.AddDays(-source.PointInTimeRecoveryPeriodInDays));
            var point = useLatest ? latest : request.RestoreDateTime!.Value.ToUniversalTime();
            if (point < earliest || point > latest)
            {
                throw new DynamoDbException(
                    "InvalidRestoreTimeException",
                    $"RestoreDateTime '{JavaRendering.Timestamp(point)}' must be within EarliestRestorableDateTime '{JavaRendering.Timestamp(earliest)}' and LatestRestorableDateTime '{JavaRendering.Timestamp(latest)}' for table '{source.Name}'");
            }

            if (Store.Tables.ContainsKey(name))
            {
                throw new DynamoDbException("TableAlreadyExistsException", $"Table already exists: {name}");
            }

            var table = Copy(source, name, TableArn(name), Guid.NewGuid().ToString(), source.ItemsAt(point).Select(e => (e.Key, e.Value)));
            if (request.BillingModeOverride is { } billing)
            {
                table.BillingMode = billing;
                table.BillingModeStated = true;
            }

            if (request.ProvisionedThroughputOverride is { } throughput)
            {
                table.Throughput = new DynamoThroughput { ReadCapacityUnits = throughput.ReadCapacityUnits ?? 0, WriteCapacityUnits = throughput.WriteCapacityUnits ?? 0 };
            }

            table.Restore = new DynamoRestore(null, source.Arn, point);
            Store.Tables[name] = table;
            Record("RestoreTableToPointInTime", table.Arn);
            return Task.FromResult(new RestoreTableToPointInTimeResponse { TableDescription = Describe(table) });
        }
    }

    /// <summary>A copy of a table's settings and items (or the given items), under another name.</summary>
    private DynamoTable Copy(DynamoTable source, string name, string arn, string tableId, IEnumerable<(DynamoKey Key, Dictionary<string, DynamoValue> Item)>? items = null)
    {
        var copy = new DynamoTable
        {
            Name = name,
            Arn = arn,
            TableId = tableId,
            CreationDateTime = string.Equals(arn, source.Arn, StringComparison.Ordinal) ? source.CreationDateTime : Now,
            KeySchema = source.KeySchema,
            AttributeDefinitions = source.AttributeDefinitions.ToList(),
            BillingMode = source.BillingMode,
            BillingModeStated = source.BillingModeStated,
            LastUpdateToPayPerRequestDateTime = source.LastUpdateToPayPerRequestDateTime,
            Throughput = new DynamoThroughput { ReadCapacityUnits = source.Throughput.ReadCapacityUnits, WriteCapacityUnits = source.Throughput.WriteCapacityUnits },
            MaxReadRequestUnits = source.MaxReadRequestUnits,
            MaxWriteRequestUnits = source.MaxWriteRequestUnits,
            TableClass = source.TableClass,
            SseEnabled = source.SseEnabled,
            SseType = source.SseType,
            SseKmsKeyArn = source.SseKmsKeyArn,
        };

        foreach (var index in source.LocalIndexes)
        {
            copy.LocalIndexes.Add(new DynamoIndex { Name = index.Name, Arn = $"{arn}/index/{index.Name}", IsGlobal = false, KeySchema = index.KeySchema, Projection = index.Projection });
        }

        foreach (var index in source.GlobalIndexes)
        {
            copy.GlobalIndexes.Add(new DynamoIndex
            {
                Name = index.Name,
                Arn = $"{arn}/index/{index.Name}",
                IsGlobal = true,
                KeySchema = index.KeySchema,
                Projection = index.Projection,
                Throughput = index.Throughput is null ? null : new DynamoThroughput { ReadCapacityUnits = index.Throughput.ReadCapacityUnits, WriteCapacityUnits = index.Throughput.WriteCapacityUnits },
                MaxReadRequestUnits = index.MaxReadRequestUnits,
                MaxWriteRequestUnits = index.MaxWriteRequestUnits,
            });
        }

        foreach (var (key, item) in items ?? source.All())
        {
            copy.Put(key, item);
        }

        return copy;
    }

    // Resource policies

    /// <summary>
    /// The table a resource-policy <c>ResourceArn</c> names: the table itself or its stream.
    /// A value that isn't an ARN, an ARN of another resource (an index, another service) and
    /// an ARN outside this account and region are refused as input, each in its own words;
    /// only a well-formed ARN of a missing table or stream is a lookup miss.
    /// </summary>
    private DynamoTable TableForPolicy(string? resourceArn)
    {
        resourceArn ??= string.Empty;
        if (!resourceArn.StartsWith("arn:", StringComparison.Ordinal))
        {
            throw DynamoDbErrors.Validation($"One or more parameter values were invalid: ARNs must start with 'arn:': {resourceArn}");
        }

        var outOfScope = DynamoDbErrors.Validation($"One or more parameter values were invalid: Invalid resource Arn: {resourceArn}");
        var unsupported = DynamoDbErrors.Validation(
            $"One or more parameter values were invalid: Invalid resource arn provided, only table or stream is accepted. Provided resource arn: {resourceArn}");
        var fields = resourceArn.Split(':', 6);
        if (fields.Length != 6 || fields[1].Length == 0 || fields[3].Length == 0)
        {
            throw outOfScope;
        }

        if (!string.Equals(fields[2], "dynamodb", StringComparison.Ordinal) || !fields[5].StartsWith("table/", StringComparison.Ordinal))
        {
            throw unsupported;
        }

        var path = fields[5]["table/".Length..];
        var slash = path.IndexOf('/', StringComparison.Ordinal);
        var name = slash < 0 ? path : path[..slash];
        var stream = slash >= 0;
        if (name.Length == 0 || (stream && (!path[(slash + 1)..].StartsWith("stream/", StringComparison.Ordinal) || path.Length == slash + 1 + "stream/".Length)))
        {
            throw unsupported;
        }

        if (!string.Equals(fields[3], Region, StringComparison.Ordinal) || !string.Equals(fields[4], _bus.CurrentAccountId, StringComparison.Ordinal))
        {
            throw outOfScope;
        }

        if (!Store.Tables.TryGetValue(name, out var table))
        {
            throw DynamoDbErrors.TableNotFound(name);
        }

        if (stream && (table.LatestStream is null || !string.Equals(table.LatestStream.Arn, resourceArn, StringComparison.Ordinal)))
        {
            throw DynamoDbErrors.ResourceNotFound($"Requested resource not found: Stream: {resourceArn} not found");
        }

        return table;
    }

    public Task<PutResourcePolicyResponse> PutResourcePolicyAsync(PutResourcePolicyRequest request, CancellationToken cancellationToken = default)
    {
        lock (Store.Gate)
        {
            var table = TableForPolicy(request.ResourceArn);
            if (request.Policy is { } policy && Encoding.UTF8.GetByteCount(policy) > MaxResourcePolicyBytes)
            {
                throw DynamoDbErrors.InvalidParameter($"Maximum policy size of {MaxResourcePolicyBytes} bytes exceeded");
            }

            CheckExpectedRevision(table, request.ExpectedRevisionId);
            table.ResourcePolicy = request.Policy;
            table.ResourcePolicyRevision = _bus.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            Record("PutResourcePolicy", request.ResourceArn);
            return Task.FromResult(new PutResourcePolicyResponse { RevisionId = table.ResourcePolicyRevision });
        }
    }

    public Task<GetResourcePolicyResponse> GetResourcePolicyAsync(GetResourcePolicyRequest request, CancellationToken cancellationToken = default)
    {
        lock (Store.Gate)
        {
            var table = TableForPolicy(request.ResourceArn);
            if (table.ResourcePolicy is null)
            {
                throw new DynamoDbException("PolicyNotFoundException", $"Resource-based policy not found for the provided ResourceArn: {request.ResourceArn}");
            }

            Record("GetResourcePolicy", request.ResourceArn);
            return Task.FromResult(new GetResourcePolicyResponse { Policy = table.ResourcePolicy, RevisionId = table.ResourcePolicyRevision });
        }
    }

    public Task<DeleteResourcePolicyResponse> DeleteResourcePolicyAsync(DeleteResourcePolicyRequest request, CancellationToken cancellationToken = default)
    {
        lock (Store.Gate)
        {
            var table = TableForPolicy(request.ResourceArn);
            CheckExpectedRevision(table, request.ExpectedRevisionId);

            // Deleting a policy that isn't there is a no-op success, with no revision to report.
            var revision = table.ResourcePolicyRevision;
            table.ResourcePolicy = null;
            table.ResourcePolicyRevision = null;
            Record("DeleteResourcePolicy", request.ResourceArn);
            return Task.FromResult(new DeleteResourcePolicyResponse { RevisionId = revision });
        }
    }

    private const int MaxResourcePolicyBytes = 20480;

    /// <summary>An <c>ExpectedRevisionId</c> must name the policy the table has now.</summary>
    private static void CheckExpectedRevision(DynamoTable table, string? expected)
    {
        if (expected is null || string.Equals(expected, table.ResourcePolicyRevision, StringComparison.Ordinal))
        {
            return;
        }

        throw new DynamoDbException(
            "PolicyNotFoundException",
            table.ResourcePolicy is null
                ? $"Resource-based policy not found for the provided ResourceArn: Requested update for policy with revision id {expected}, but table {table.Name} has no associated policy."
                : $"Resource-based policy not found for the provided ResourceArn: Requested update for policy with revision id {expected}, but the policy associated to target table {table.Name} has revision id {table.ResourcePolicyRevision}.");
    }

    // Contributor insights: a setting the table remembers; no metrics are collected.

    public Task<DescribeContributorInsightsResponse> DescribeContributorInsightsAsync(DescribeContributorInsightsRequest request, CancellationToken cancellationToken = default)
    {
        lock (Store.Gate)
        {
            var table = ExistingTable(request.TableName!);
            var setting = ContributorInsights(table, request.IndexName);
            Record("DescribeContributorInsights", table.Arn);
            return Task.FromResult(new DescribeContributorInsightsResponse
            {
                TableName = table.Name,
                IndexName = request.IndexName,
                ContributorInsightsStatus = setting.Status,
                ContributorInsightsMode = setting.Status == "ENABLED" ? setting.Mode : null,
                ContributorInsightsRuleList = setting.Status == "ENABLED" ? [$"DynamoDBContributorInsights-PKC-{table.Name}-{table.CreationDateTime.Ticks}"] : null,
                LastUpdateDateTime = setting.LastUpdate,
            });
        }
    }

    public Task<UpdateContributorInsightsResponse> UpdateContributorInsightsAsync(UpdateContributorInsightsRequest request, CancellationToken cancellationToken = default)
    {
        lock (Store.Gate)
        {
            var table = ExistingTable(request.TableName!);
            var setting = ContributorInsights(table, request.IndexName);
            setting.Status = request.ContributorInsightsAction == "ENABLE" ? "ENABLED" : "DISABLED";
            setting.Mode = request.ContributorInsightsMode ?? "ACCESSED_AND_THROTTLED_KEYS";
            setting.LastUpdate = Now;
            Record("UpdateContributorInsights", table.Arn);
            return Task.FromResult(new UpdateContributorInsightsResponse
            {
                TableName = table.Name,
                IndexName = request.IndexName,
                ContributorInsightsStatus = setting.Status == "ENABLED" ? "ENABLING" : "DISABLING",
                ContributorInsightsMode = setting.Mode,
            });
        }
    }

    public Task<ListContributorInsightsResponse> ListContributorInsightsAsync(ListContributorInsightsRequest request, CancellationToken cancellationToken = default)
    {
        lock (Store.Gate)
        {
            var tables = request.TableName is null ? Store.Tables.Values.ToList() : [ExistingTable(request.TableName)];
            var summaries = tables
                .SelectMany(t => t.ContributorInsights.Select(c => new ContributorInsightsSummary
                {
                    TableName = t.Name,
                    IndexName = c.Key.Length == 0 ? null : c.Key,
                    ContributorInsightsStatus = c.Value.Status,
                    ContributorInsightsMode = c.Value.Status == "ENABLED" ? c.Value.Mode : null,
                }))
                .Where(s => s.ContributorInsightsStatus == "ENABLED")
                .ToList();
            Record("ListContributorInsights");
            return Task.FromResult(new ListContributorInsightsResponse { ContributorInsightsSummaries = summaries });
        }
    }

    private static DynamoContributorInsights ContributorInsights(DynamoTable table, string? indexName)
    {
        if (indexName is not null && table.FindIndex(indexName) is null)
        {
            throw DynamoDbErrors.ResourceNotFound($"Requested resource not found: Index: {indexName} not found");
        }

        var key = indexName ?? string.Empty;
        if (!table.ContributorInsights.TryGetValue(key, out var setting))
        {
            setting = new DynamoContributorInsights();
            table.ContributorInsights[key] = setting;
        }

        return setting;
    }

    // Features of other services, answered as a table that uses none of them.

    public Task<DescribeKinesisStreamingDestinationResponse> DescribeKinesisStreamingDestinationAsync(DescribeKinesisStreamingDestinationRequest request, CancellationToken cancellationToken = default)
    {
        lock (Store.Gate)
        {
            var table = ExistingTable(request.TableName!);
            return Task.FromResult(new DescribeKinesisStreamingDestinationResponse { TableName = table.Name, KinesisDataStreamDestinations = [] });
        }
    }

    public Task<ListGlobalTablesResponse> ListGlobalTablesAsync(ListGlobalTablesRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ListGlobalTablesResponse { GlobalTables = [] });

    public Task<DescribeGlobalTableResponse> DescribeGlobalTableAsync(DescribeGlobalTableRequest request, CancellationToken cancellationToken = default) =>
        throw new DynamoDbException("GlobalTableNotFoundException", "Global table not found");

    public Task<ListExportsResponse> ListExportsAsync(ListExportsRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ListExportsResponse { ExportSummaries = [] });

    public Task<ListImportsResponse> ListImportsAsync(ListImportsRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ListImportsResponse { ImportSummaryList = [] });

    public Task<DescribeExportResponse> DescribeExportAsync(DescribeExportRequest request, CancellationToken cancellationToken = default) =>
        throw new DynamoDbException("ExportNotFoundException", $"Export not found: {request.ExportArn}");

    public Task<DescribeImportResponse> DescribeImportAsync(DescribeImportRequest request, CancellationToken cancellationToken = default) =>
        throw new DynamoDbException("ImportNotFoundException", $"Import not found: {request.ImportArn}");

    public Task<DescribeTableReplicaAutoScalingResponse> DescribeTableReplicaAutoScalingAsync(DescribeTableReplicaAutoScalingRequest request, CancellationToken cancellationToken = default)
    {
        lock (Store.Gate)
        {
            var table = ExistingTable(request.TableName!);
            throw DynamoDbErrors.ResourceNotFound($"Requested resource not found: Table: {table.Name} is not a global table");
        }
    }
}

/// <summary>A table's (or index's) contributor insights setting.</summary>
internal sealed class DynamoContributorInsights
{
    public string Status { get; set; } = "DISABLED";

    public string Mode { get; set; } = "ACCESSED_AND_THROTTLED_KEYS";

    public DateTime? LastUpdate { get; set; }
}

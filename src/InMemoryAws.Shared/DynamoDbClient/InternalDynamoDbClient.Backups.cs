using System.Text;
using InMemoryAws.DynamoDb.Model;

#pragma warning disable CA1822 // The generated dispatcher calls every operation on a client instance.

namespace InMemoryAws.DynamoDb;

/// <summary>
/// Backups and the table settings that need no other service: on-demand backups and restores,
/// point-in-time recovery settings, resource policies, contributor insights, and the empty
/// listings of global tables, exports, imports and Kinesis destinations.
/// </summary>
internal sealed partial class InternalDynamoDbClient
{
    /// <summary>
    /// The table a backup operation names. One still being created, or being deleted, is not
    /// there as far as backups go.
    /// </summary>
    private DynamoTable TableForBackup(string? tableName)
    {
        var name = TableName(tableName!);
        return FindTable(name) is { Status: not ("CREATING" or "DELETING") } table
            ? table
            : throw new DynamoDbException("TableNotFoundException", $"Table not found: {name}");
    }

    public Task<DescribeContinuousBackupsResponse> DescribeContinuousBackupsAsync(DescribeContinuousBackupsRequest request, CancellationToken cancellationToken = default)
    {
        lock (Store.Gate)
        {
            var table = TableForBackup(request.TableName);
            Authorize(table, "DescribeContinuousBackups");
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
                EarliestRestorableDateTime = EarliestRestorableDateTime(table),
                LatestRestorableDateTime = LatestRestorableDateTime(table),
            }
            : new PointInTimeRecoveryDescription { PointInTimeRecoveryStatus = "DISABLED" },
    };

    public Task<UpdateContinuousBackupsResponse> UpdateContinuousBackupsAsync(UpdateContinuousBackupsRequest request, CancellationToken cancellationToken = default)
    {
        lock (Store.Gate)
        {
            var table = TableForBackup(request.TableName);
            Authorize(table, "UpdateContinuousBackups");
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
                table.PointInTimeRecoveryEnabledAt = new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
                table.PointInTimeLog = new DynamoPointInTimeLog(_cloud.TimeProvider, TimeSpan.FromDays(specification.RecoveryPeriodInDays ?? table.PointInTimeRecoveryPeriodInDays));
            }
            else if (!enable)
            {
                table.PointInTimeRecoveryEnabledAt = null;
                table.PointInTimeLog = null;
            }

            table.PointInTimeRecoveryEnabled = enable;
            table.PointInTimeRecoveryPeriodInDays = specification.RecoveryPeriodInDays ?? (enable ? table.PointInTimeRecoveryPeriodInDays : 35);
            if (table.PointInTimeLog is { } log)
            {
                log.Retention = TimeSpan.FromDays(table.PointInTimeRecoveryPeriodInDays);
            }

            Record("UpdateContinuousBackups", table.Arn);
            return Task.FromResult(new UpdateContinuousBackupsResponse { ContinuousBackupsDescription = ContinuousBackups(table) });
        }
    }

    public Task<CreateBackupResponse> CreateBackupAsync(CreateBackupRequest request, CancellationToken cancellationToken = default)
    {
        lock (Store.Gate)
        {
            var table = TableForBackup(request.TableName);
            Authorize(table, "CreateBackup");
            var created = Now;
            var arn = $"{table.Arn}/backup/{created.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture)}-{Guid.NewGuid().ToString("N")[..8]}";
            var backup = new DynamoBackup
            {
                Arn = arn,
                Name = request.BackupName!,
                CreationDateTime = created,
                Snapshot = Copy(table, table.Name, table.Arn, table.TableId, table.CreationDateTime),
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
            if (AllTables().Any(t => t.Status == "CREATING" && string.Equals(t.Restore?.SourceBackupArn, backup.Arn, StringComparison.Ordinal)))
            {
                throw new DynamoDbException("BackupInUseException", $"Backup is being used to restore table: {backup.Arn}");
            }

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
            if (FindTable(name) is not null)
            {
                throw new DynamoDbException("TableAlreadyExistsException", $"Table already exists: {name}");
            }

            var table = Copy(backup.Snapshot, name, TableArn(name), Guid.NewGuid().ToString(), Now);
            ApplyRestoreOverrides(
                table,
                request.BillingModeOverride,
                request.ProvisionedThroughputOverride,
                request.OnDemandThroughputOverride,
                request.GlobalSecondaryIndexOverride,
                request.LocalSecondaryIndexOverride,
                request.SSESpecificationOverride,
                request.VectorIndexOverride);
            table.Restore = new DynamoRestore(backup.Arn, backup.TableArn, Now);
            if (TransitionsTakeTime)
            {
                table.StartCreating(TransitionEnd);
            }

            Store.Tables[name] = table;
            Record("RestoreTableFromBackup", table.Arn);
            return Task.FromResult(new RestoreTableFromBackupResponse { TableDescription = Describe(table) });
        }
    }

    /// <summary>
    /// The earliest time a table can be restored to: when recovery was turned on, or the start of
    /// the recovery period, whichever is later (rounded up to the second, so it reads back exactly
    /// as a RestoreDateTime whose changes are all still in the log).
    /// </summary>
    private DateTime? EarliestRestorableDateTime(DynamoTable table)
    {
        if (table.PointInTimeRecoveryEnabledAt is not { } enabledAt)
        {
            return null;
        }

        var periodStart = Now.AddDays(-table.PointInTimeRecoveryPeriodInDays);
        var remainder = periodStart.Ticks % TimeSpan.TicksPerSecond;
        if (remainder != 0)
        {
            periodStart = new DateTime(periodStart.Ticks - remainder + TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        }

        return periodStart > enabledAt ? periodStart : enabledAt;
    }

    /// <summary>The latest time a table can be restored to: five minutes ago, as DynamoDB reports it, but never before the earliest.</summary>
    private DateTime LatestRestorableDateTime(DynamoTable table)
    {
        var latest = Now.AddMinutes(-5);
        return EarliestRestorableDateTime(table) is { } earliest && latest < earliest ? earliest : latest;
    }

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
            if (!source.PointInTimeRecoveryEnabled || EarliestRestorableDateTime(source) is not { } earliest)
            {
                throw new DynamoDbException("PointInTimeRecoveryUnavailableException", $"Point in time recovery is not enabled for table '{source.Name}'");
            }

            var latest = LatestRestorableDateTime(source);
            var point = useLatest ? latest : request.RestoreDateTime!.Value.ToUniversalTime();
            if (point < earliest || point > latest)
            {
                throw new DynamoDbException(
                    "InvalidRestoreTimeException",
                    $"RestoreDateTime '{JavaRendering.Timestamp(point)}' must be within EarliestRestorableDateTime '{JavaRendering.Timestamp(earliest)}' and LatestRestorableDateTime '{JavaRendering.Timestamp(latest)}' for table '{source.Name}'");
            }

            if (FindTable(name) is not null)
            {
                throw new DynamoDbException("TableAlreadyExistsException", $"Table already exists: {name}");
            }

            var table = Copy(source, name, TableArn(name), Guid.NewGuid().ToString(), Now, source.ItemsAt(point).Select(e => (e.Key, e.Value)));
            ApplyRestoreOverrides(
                table,
                request.BillingModeOverride,
                request.ProvisionedThroughputOverride,
                request.OnDemandThroughputOverride,
                request.GlobalSecondaryIndexOverride,
                request.LocalSecondaryIndexOverride,
                request.SSESpecificationOverride,
                request.VectorIndexOverride);
            table.Restore = new DynamoRestore(null, source.Arn, point);
            if (TransitionsTakeTime)
            {
                table.StartCreating(TransitionEnd);
            }

            Store.Tables[name] = table;
            Record("RestoreTableToPointInTime", table.Arn);
            return Task.FromResult(new RestoreTableToPointInTimeResponse { TableDescription = Describe(table) });
        }
    }

    /// <summary>
    /// Applies a restore's overrides to the restored table. An index override names which of the
    /// source's indexes the restored table keeps (DynamoDB allows leaving some or all out, not
    /// adding new ones); a global index's override may also set its throughput.
    /// </summary>
    private void ApplyRestoreOverrides(
        DynamoTable table,
        string? billingMode,
        ProvisionedThroughput? throughput,
        OnDemandThroughput? onDemandThroughput,
        List<GlobalSecondaryIndex>? globalIndexes,
        List<LocalSecondaryIndex>? localIndexes,
        SSESpecification? sse,
        List<VectorIndex>? vectorIndexes)
    {
        if (localIndexes is not null)
        {
            var kept = new List<DynamoIndex>();
            foreach (var local in localIndexes)
            {
                kept.Add(table.LocalIndexes.FirstOrDefault(i => string.Equals(i.Name, local.IndexName, StringComparison.Ordinal))
                    ?? throw DynamoDbErrors.InvalidParameter($"LocalSecondaryIndexOverride names an index the source table does not have: {local.IndexName}"));
            }

            table.LocalIndexes.Clear();
            table.LocalIndexes.AddRange(kept);
        }

        if (globalIndexes is not null)
        {
            var kept = new List<DynamoIndex>();
            foreach (var global in globalIndexes)
            {
                var index = table.GlobalIndexes.FirstOrDefault(i => string.Equals(i.Name, global.IndexName, StringComparison.Ordinal))
                    ?? throw DynamoDbErrors.InvalidParameter($"GlobalSecondaryIndexOverride names an index the source table does not have: {global.IndexName}");
                if (global.ProvisionedThroughput is { } indexThroughput)
                {
                    index.Throughput = new DynamoThroughput { ReadCapacityUnits = indexThroughput.ReadCapacityUnits ?? 0, WriteCapacityUnits = indexThroughput.WriteCapacityUnits ?? 0 };
                }

                if (global.OnDemandThroughput is { } indexOnDemand)
                {
                    index.MaxReadRequestUnits = indexOnDemand.MaxReadRequestUnits;
                    index.MaxWriteRequestUnits = indexOnDemand.MaxWriteRequestUnits;
                }

                kept.Add(index);
            }

            table.GlobalIndexes.Clear();
            table.GlobalIndexes.AddRange(kept);
        }

        if (vectorIndexes is not null)
        {
            var kept = new List<DynamoVectorIndex>();
            foreach (var vector in vectorIndexes)
            {
                kept.Add(table.FindVectorIndex(vector.IndexName!)
                    ?? throw DynamoDbErrors.InvalidParameter($"VectorIndexOverride names an index the source table does not have: {vector.IndexName}"));
            }

            table.VectorIndexes.Clear();
            table.VectorIndexes.AddRange(kept);
        }

        if (localIndexes is not null || globalIndexes is not null || vectorIndexes is not null)
        {
            // An attribute only a dropped index was keyed on is no longer defined.
            var used = table.UsedAttributeNames();
            table.AttributeDefinitions = table.AttributeDefinitions.Where(a => used.Contains(a.Name)).ToList();
        }

        if (billingMode is not null)
        {
            // Vector indexes need on-demand billing, so a provisioned restore must leave them all out.
            if (billingMode != "PAY_PER_REQUEST" && table.VectorIndexes.Count > 0)
            {
                throw DynamoDbErrors.InvalidParameter(VectorIndexesNeedOnDemand);
            }

            if (billingMode == "PAY_PER_REQUEST" && table.BillingMode != "PAY_PER_REQUEST")
            {
                table.LastUpdateToPayPerRequestDateTime = Now;
                table.Throughput = new DynamoThroughput();
                foreach (var index in table.GlobalIndexes)
                {
                    index.Throughput = new DynamoThroughput();
                }
            }

            table.BillingMode = billingMode;
            table.BillingModeStated = true;
        }

        if (throughput is not null)
        {
            table.Throughput = new DynamoThroughput { ReadCapacityUnits = throughput.ReadCapacityUnits ?? 0, WriteCapacityUnits = throughput.WriteCapacityUnits ?? 0 };
        }

        if (onDemandThroughput is not null)
        {
            table.MaxReadRequestUnits = onDemandThroughput.MaxReadRequestUnits;
            table.MaxWriteRequestUnits = onDemandThroughput.MaxWriteRequestUnits;
        }

        if (sse is not null)
        {
            SetServerSideEncryption(table, sse.Enabled ?? true, sse);
        }
    }

    /// <summary>A copy of a table's settings and items (or the given items), under another name.</summary>
    private static DynamoTable Copy(DynamoTable source, string name, string arn, string tableId, DateTime creationDateTime, IEnumerable<(DynamoKey Key, Dictionary<string, DynamoValue> Item)>? items = null)
    {
        var copy = new DynamoTable
        {
            Name = name,
            Arn = arn,
            TableId = tableId,
            CreationDateTime = creationDateTime,
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

        // Only a vector index that is built is restored: one still CREATING has nothing to restore yet.
        foreach (var index in source.VectorIndexes.Where(v => v.Status == "ACTIVE"))
        {
            copy.VectorIndexes.Add(new DynamoVectorIndex
            {
                Name = index.Name,
                Arn = $"{arn}/index/{index.Name}",
                VectorAttribute = index.VectorAttribute,
                Dimensions = index.Dimensions,
                DistanceFunction = index.DistanceFunction,
                Projection = index.Projection,
                SearchSchema = index.SearchSchema,
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
    /// The policy of the resource a resource-policy <c>ResourceArn</c> names: a table, or its stream
    /// (each has its own), with the name of the table.
    /// A value that isn't an ARN, an ARN of another resource (an index, another service) and
    /// an ARN outside this account and region are refused as input, each in its own words;
    /// only a well-formed ARN of a missing table or stream is a lookup miss.
    /// </summary>
    private (string TableName, DynamoResourcePolicy Policy) PolicyOf(string? resourceArn)
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

        if (!string.Equals(fields[3], Region, StringComparison.Ordinal) || !string.Equals(fields[4], _cloud.CurrentAccountId, StringComparison.Ordinal))
        {
            throw outOfScope;
        }

        if (FindTable(name) is not { } table)
        {
            throw DynamoDbErrors.TableNotFound(name);
        }

        if (stream && (table.LatestStream is null || !string.Equals(table.LatestStream.Arn, resourceArn, StringComparison.Ordinal)))
        {
            throw DynamoDbErrors.ResourceNotFound($"Requested resource not found: Stream: {resourceArn} not found");
        }

        return (table.Name, stream ? table.LatestStream!.ResourcePolicy : table.ResourcePolicy);
    }

    public Task<PutResourcePolicyResponse> PutResourcePolicyAsync(PutResourcePolicyRequest request, CancellationToken cancellationToken = default)
    {
        lock (Store.Gate)
        {
            var (tableName, resourcePolicy) = PolicyOf(request.ResourceArn);
            if (request.Policy is { } policy)
            {
                CheckResourcePolicySize(policy);
            }

            CheckExpectedRevision(tableName, resourcePolicy, request.ExpectedRevisionId);
            resourcePolicy.Set(request.Policy, _cloud.TimeProvider);
            Record("PutResourcePolicy", request.ResourceArn);
            return Task.FromResult(new PutResourcePolicyResponse { RevisionId = resourcePolicy.Revision });
        }
    }

    public Task<GetResourcePolicyResponse> GetResourcePolicyAsync(GetResourcePolicyRequest request, CancellationToken cancellationToken = default)
    {
        lock (Store.Gate)
        {
            var (_, resourcePolicy) = PolicyOf(request.ResourceArn);
            if (resourcePolicy.Policy is null)
            {
                throw new DynamoDbException("PolicyNotFoundException", $"Resource-based policy not found for the provided ResourceArn: {request.ResourceArn}");
            }

            Record("GetResourcePolicy", request.ResourceArn);
            return Task.FromResult(new GetResourcePolicyResponse { Policy = resourcePolicy.Policy, RevisionId = resourcePolicy.Revision });
        }
    }

    public Task<DeleteResourcePolicyResponse> DeleteResourcePolicyAsync(DeleteResourcePolicyRequest request, CancellationToken cancellationToken = default)
    {
        lock (Store.Gate)
        {
            var (tableName, resourcePolicy) = PolicyOf(request.ResourceArn);
            CheckExpectedRevision(tableName, resourcePolicy, request.ExpectedRevisionId);

            // Deleting a policy that isn't there is a no-op success, with no revision to report.
            var revision = resourcePolicy.Revision;
            resourcePolicy.Set(null, _cloud.TimeProvider);
            Record("DeleteResourcePolicy", request.ResourceArn);
            return Task.FromResult(new DeleteResourcePolicyResponse { RevisionId = revision });
        }
    }

    private const int MaxResourcePolicyBytes = 20480;

    /// <summary>The value of <c>ExpectedRevisionId</c> that asks for a policy to be attached only where there is none.</summary>
    private const string NoPolicyRevision = "NO_POLICY";

    private static void CheckResourcePolicySize(string policy)
    {
        if (Encoding.UTF8.GetByteCount(policy) > MaxResourcePolicyBytes)
        {
            throw DynamoDbErrors.InvalidParameter($"Maximum policy size of {MaxResourcePolicyBytes} bytes exceeded");
        }
    }

    /// <summary>
    /// An <c>ExpectedRevisionId</c> must name the policy the resource has now, or be
    /// <c>NO_POLICY</c> for a resource with none.
    /// </summary>
    private static void CheckExpectedRevision(string tableName, DynamoResourcePolicy resourcePolicy, string? expected)
    {
        if (expected is null
            || string.Equals(expected, resourcePolicy.Revision, StringComparison.Ordinal)
            || (string.Equals(expected, NoPolicyRevision, StringComparison.Ordinal) && resourcePolicy.Policy is null))
        {
            return;
        }

        throw new DynamoDbException(
            "PolicyNotFoundException",
            resourcePolicy.Policy is null
                ? $"Resource-based policy not found for the provided ResourceArn: Requested update for policy with revision id {expected}, but table {tableName} has no associated policy."
                : $"Resource-based policy not found for the provided ResourceArn: Requested update for policy with revision id {expected}, but the policy associated to target table {tableName} has revision id {resourcePolicy.Revision}.");
    }

    // Contributor insights: a setting the table remembers; no metrics are collected.

    public Task<DescribeContributorInsightsResponse> DescribeContributorInsightsAsync(DescribeContributorInsightsRequest request, CancellationToken cancellationToken = default)
    {
        lock (Store.Gate)
        {
            var table = ExistingTable(request.TableName!);
            Authorize(table, "DescribeContributorInsights", IndexArn(table, request.IndexName));
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
            Authorize(table, "UpdateContributorInsights", IndexArn(table, request.IndexName));
            var setting = ContributorInsights(table, request.IndexName);
            var enable = request.ContributorInsightsAction == "ENABLE";
            if (!enable && setting.Status is "ENABLING" or "DISABLING")
            {
                throw DynamoDbErrors.Validation("Table or Index is not in a valid state to update Key Access Insights: ContributorInsights Status must be ENABLED or FAILED to perform DISABLE operation.");
            }

            setting.Start(enable, request.ContributorInsightsMode ?? "ACCESSED_AND_THROTTLED_KEYS", TransitionEnd);
            setting.LastUpdate = Now;
            Record("UpdateContributorInsights", table.Arn);
            return Task.FromResult(new UpdateContributorInsightsResponse
            {
                TableName = table.Name,
                IndexName = request.IndexName,
                ContributorInsightsStatus = enable ? "ENABLING" : "DISABLING",
                ContributorInsightsMode = setting.Mode,
            });
        }
    }

    public Task<ListContributorInsightsResponse> ListContributorInsightsAsync(ListContributorInsightsRequest request, CancellationToken cancellationToken = default)
    {
        lock (Store.Gate)
        {
            var tables = request.TableName is null ? AllTables() : [ExistingTable(request.TableName)];
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
            Authorize(table, "DescribeKinesisStreamingDestination");
            return Task.FromResult(new DescribeKinesisStreamingDestinationResponse { TableName = table.Name, KinesisDataStreamDestinations = [] });
        }
    }

    public Task<ListExportsResponse> ListExportsAsync(ListExportsRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ListExportsResponse { ExportSummaries = [] });

    public Task<ListImportsResponse> ListImportsAsync(ListImportsRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ListImportsResponse { ImportSummaryList = [] });

    public Task<DescribeExportResponse> DescribeExportAsync(DescribeExportRequest request, CancellationToken cancellationToken = default) =>
        throw new DynamoDbException("ExportNotFoundException", $"Export not found: {request.ExportArn}");

    public Task<DescribeImportResponse> DescribeImportAsync(DescribeImportRequest request, CancellationToken cancellationToken = default) =>
        throw new DynamoDbException("ImportNotFoundException", $"Import not found: {request.ImportArn}");
}

/// <summary>
/// A table's (or index's) contributor insights setting: <c>ENABLING</c> or <c>DISABLING</c> for a
/// while after a change, then <c>ENABLED</c> or <c>DISABLED</c>.
/// </summary>
internal sealed class DynamoContributorInsights
{
    private DateTimeOffset _settlesAt;

    public string Status { get; private set; } = "DISABLED";

    public string Mode { get; private set; } = "ACCESSED_AND_THROTTLED_KEYS";

    /// <summary>Starts enabling or disabling the setting, done at <paramref name="settlesAt"/>.</summary>
    public void Start(bool enable, string mode, DateTimeOffset settlesAt)
    {
        Status = enable ? "ENABLING" : "DISABLING";
        Mode = mode;
        _settlesAt = settlesAt;
    }

    /// <summary>Settles an <c>ENABLING</c> or <c>DISABLING</c> setting once its time has come.</summary>
    public void Settle(DateTimeOffset now)
    {
        if (now >= _settlesAt)
        {
            Status = Status switch
            {
                "ENABLING" => "ENABLED",
                "DISABLING" => "DISABLED",
                _ => Status,
            };
        }
    }

    public DateTime? LastUpdate { get; set; }
}

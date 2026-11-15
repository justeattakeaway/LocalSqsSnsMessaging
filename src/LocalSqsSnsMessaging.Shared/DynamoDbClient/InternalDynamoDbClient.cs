using LocalSqsSnsMessaging.DynamoDb.Model;

namespace LocalSqsSnsMessaging.DynamoDb;

/// <summary>
/// In-memory DynamoDB, serving the generated handler's requests for one region.
/// </summary>
/// <remarks>
/// Split into partial files by area: tables, items, reads (Query and Scan), batches and
/// transactions, PartiQL, time to live, backups, authorization against a table's resource policy,
/// and the operations the bus doesn't emulate. Every operation runs under its region's
/// <see cref="DynamoDbStore.Gate"/>.
/// </remarks>
internal sealed partial class InternalDynamoDbClient
{
    private readonly InMemoryAwsBus _bus;
    private readonly AwsRegionResources _resources;

    /// <param name="bus">The bus holding the tables.</param>
    /// <param name="region">The region the request was made in; null for the bus's current region.</param>
    internal InternalDynamoDbClient(InMemoryAwsBus bus, string? region = null)
    {
        _bus = bus;
        _resources = bus.Region(region);
    }

    private DynamoDbStore Store => _resources.DynamoDb;

    private string Region => _resources.Region;

    private DateTime Now => _bus.TimeProvider.GetUtcNow().UtcDateTime;

    private string TableArn(string name) => $"arn:aws:dynamodb:{Region}:{_bus.CurrentAccountId}:table/{name}";

    private void Record(string action, string? arn = null) => _bus.RecordOperation(AwsServiceName.DynamoDb, action, arn);

    /// <summary>
    /// The table name a <c>TableName</c> member names: a name, or a table ARN (which must be this
    /// region's and account's).
    /// </summary>
    private string TableName(string tableName)
    {
        if (!tableName.StartsWith("arn:", StringComparison.Ordinal))
        {
            return tableName;
        }

        // arn:<partition>:<vendor>:<region>:<account>:<resource>, every field present.
        var fields = tableName["arn:".Length..].Split([':'], 5);
        if (fields.Length != 5 || fields.Any(f => f.Length == 0))
        {
            throw DynamoDbErrors.ValidationClause(AwsValidation.Clause(tableName, "tableName", TableArnFormat));
        }

        if (!string.Equals(fields[1], "dynamodb", StringComparison.Ordinal))
        {
            // An ARN of another service is no way of naming a table, so it is refused as the name it would have to be.
            throw DynamoDbErrors.ValidationClause(AwsValidation.Clause(tableName, "tableName", "Member must satisfy regular expression pattern: [a-zA-Z0-9_.-]+"));
        }

        // A DynamoDB ARN of another resource type, or of a table's sub-resource (an index, a stream).
        var resource = fields[4];
        var slash = resource.IndexOf('/', StringComparison.Ordinal);
        var name = slash < 0 ? string.Empty : resource[(slash + 1)..];
        if (slash < 0 || !string.Equals(resource[..slash], "table", StringComparison.Ordinal) || name.Length == 0 || name.Contains('/', StringComparison.Ordinal))
        {
            throw DynamoDbErrors.ValidationClause(AwsValidation.Clause(tableName, "tableName", "Invalid resource type"));
        }

        if (!string.Equals(fields[2], Region, StringComparison.Ordinal) || !string.Equals(fields[3], _bus.CurrentAccountId, StringComparison.Ordinal))
        {
            throw DynamoDbErrors.ResourceNotFound();
        }

        return name;
    }

    private const string TableArnFormat =
        "Valid ARN format is 'arn:<awsPartition>:<vendor>:<region>:<subscriber>:resourceType/resourceName', where Resource name must have length less than or equal to 255, Resource name must have length greater than or equal to 3, Resource name must satisfy regular expression pattern: [a-zA-Z0-9_.-]+";

    /// <summary>Checks a table name as DynamoDB does: 3 to 255 characters of <c>[a-zA-Z0-9_.-]</c>.</summary>
    private static void CheckTableName(string name, string member = "tableName")
    {
        var clauses = new List<string>();
        if (name.Length < 3)
        {
            clauses.Add(AwsValidation.Clause(name, member, "Member must have length greater than or equal to 3"));
        }

        if (name.Length > 255)
        {
            clauses.Add(AwsValidation.Clause(name, member, "Member must have length less than or equal to 255"));
        }

        if (name.Length == 0 || name.Any(c => !(c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '.' or '-')))
        {
            clauses.Add(AwsValidation.Clause(name, member, "Member must satisfy regular expression pattern: [a-zA-Z0-9_.-]+"));
        }

        AwsValidation.ThrowIfAny(clauses);
    }

    /// <summary>A table a data-plane request names: refused as "Requested resource not found" when absent or not yet ACTIVE.</summary>
    private DynamoTable ActiveTable(string tableName)
    {
        var name = TableName(tableName);
        if (!Store.Tables.TryGetValue(name, out var table))
        {
            CheckTableName(name);
            throw DynamoDbErrors.TableNotFound();
        }

        ApplyDueTransitions(table);
        if (table.Status is "CREATING" or "DELETING")
        {
            throw DynamoDbErrors.TableNotFound();
        }

        return table;
    }

    /// <summary>A table a control-plane request names: refused with the table's name when absent.</summary>
    private DynamoTable ExistingTable(string tableName)
    {
        var name = TableName(tableName);
        if (!Store.Tables.TryGetValue(name, out var table))
        {
            throw DynamoDbErrors.TableNotFound(name);
        }

        ApplyDueTransitions(table);
        return table;
    }

    /// <summary>Moves a table created with a creation delay to ACTIVE once its time has come.</summary>
    private void ApplyDueTransitions(DynamoTable table)
    {
        if (table.ActiveAt is { } activeAt && _bus.TimeProvider.GetUtcNow() >= activeAt)
        {
            table.ActiveAt = null;
            table.Status = "ACTIVE";
            foreach (var index in table.Indexes)
            {
                index.Status = "ACTIVE";
            }
        }
    }

    // Capacity: reads cost a unit per 4 KB (half for an eventually consistent read), writes a unit per 1 KB.

    private static double ReadUnits(long bytes, bool consistent, bool transactional = false)
    {
        var units = Math.Max(1, (long)Math.Ceiling(bytes / 4096.0));
        var cost = consistent ? units : units / 2.0;
        return transactional ? units * 2.0 : cost;
    }

    private static double WriteUnits(long bytes, bool transactional = false)
    {
        var units = Math.Max(1, (long)Math.Ceiling(bytes / 1024.0));
        return transactional ? units * 2.0 : units;
    }

    /// <summary>What an item write costs: the larger of the item before and after.</summary>
    private static double WriteUnits(IReadOnlyDictionary<string, DynamoValue>? before, IReadOnlyDictionary<string, DynamoValue>? after, bool transactional = false) =>
        WriteUnits(Math.Max(before is null ? 0 : DynamoValueRules.ItemSize(before), after is null ? 0 : DynamoValueRules.ItemSize(after)), transactional);

    /// <summary>
    /// The <c>ConsumedCapacity</c> to answer with: none for <c>NONE</c>, the total for
    /// <c>TOTAL</c>, and the table's and each index's share as well for <c>INDEXES</c>.
    /// </summary>
    /// <param name="transactional">
    /// Whether a transaction consumed it: DynamoDB reports a transaction's units by kind
    /// (<c>ReadCapacityUnits</c>, <c>WriteCapacityUnits</c>), every other operation's as
    /// <c>CapacityUnits</c> alone.
    /// </param>
    private static ConsumedCapacity? Consumed(
        string? returnConsumedCapacity,
        DynamoTable table,
        double tableUnits,
        bool write,
        IReadOnlyDictionary<string, double>? indexUnits = null,
        bool transactional = false)
    {
        if (returnConsumedCapacity is null or "NONE")
        {
            return null;
        }

        var total = tableUnits + (indexUnits?.Values.Sum() ?? 0);
        var consumed = new ConsumedCapacity
        {
            TableName = table.Name,
            CapacityUnits = total,
            ReadCapacityUnits = !transactional || write ? null : total,
            WriteCapacityUnits = transactional && write ? total : null,
        };

        if (returnConsumedCapacity == "INDEXES")
        {
            consumed.Table = Capacity(tableUnits, write, transactional);
            if (indexUnits is not null)
            {
                foreach (var (name, units) in indexUnits)
                {
                    var index = table.FindIndex(name);
                    if (index is { IsGlobal: true })
                    {
                        (consumed.GlobalSecondaryIndexes ??= new Dictionary<string, Model.Capacity>(StringComparer.Ordinal))[name] = Capacity(units, write, transactional);
                    }
                    else
                    {
                        (consumed.LocalSecondaryIndexes ??= new Dictionary<string, Model.Capacity>(StringComparer.Ordinal))[name] = Capacity(units, write, transactional);
                    }
                }
            }
        }

        return consumed;
    }

    private static Model.Capacity Capacity(double units, bool write, bool transactional) => new()
    {
        CapacityUnits = units,
        ReadCapacityUnits = !transactional || write ? null : units,
        WriteCapacityUnits = transactional && write ? units : null,
    };

    /// <summary>Adds what an item write costs each index it changes to a running per-table, per-index total.</summary>
    private static void AddIndexWriteUnits(
        Dictionary<DynamoTable, Dictionary<string, double>> totals,
        DynamoTable table,
        IReadOnlyDictionary<string, DynamoValue>? before,
        IReadOnlyDictionary<string, DynamoValue>? after)
    {
        if (!totals.TryGetValue(table, out var sums))
        {
            totals[table] = sums = new Dictionary<string, double>(StringComparer.Ordinal);
        }

        foreach (var (index, cost) in IndexWriteUnits(table, before, after))
        {
            sums[index] = (sums.TryGetValue(index, out var sum) ? sum : 0) + cost;
        }
    }

    /// <summary>What an item write costs each global index it changes: a unit per 1 KB of the index entry, twice when the entry moves.</summary>
    private static Dictionary<string, double> IndexWriteUnits(DynamoTable table, IReadOnlyDictionary<string, DynamoValue>? before, IReadOnlyDictionary<string, DynamoValue>? after)
    {
        var units = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var index in table.Indexes)
        {
            var oldKey = before is null ? null : index.KeySchema.KeyOf(before);
            var newKey = after is null ? null : index.KeySchema.KeyOf(after);
            if (oldKey is null && newKey is null)
            {
                continue;
            }

            // An entry that keeps its key and every projected attribute isn't rewritten, so costs the index nothing.
            if (oldKey is not null && newKey is not null && oldKey.Equals(newKey) && SameEntry(ProjectForIndex(table, index, before!), ProjectForIndex(table, index, after!)))
            {
                continue;
            }

            var cost = 0.0;
            if (oldKey is not null)
            {
                cost += WriteUnits(DynamoValueRules.ItemSize(ProjectForIndex(table, index, before!)));
            }

            if (newKey is not null && (oldKey is null || !oldKey.Equals(newKey) || !ReferenceEquals(before, after)))
            {
                cost = oldKey is not null && oldKey.Equals(newKey)
                    ? WriteUnits(DynamoValueRules.ItemSize(ProjectForIndex(table, index, after!)))
                    : cost + WriteUnits(DynamoValueRules.ItemSize(ProjectForIndex(table, index, after!)));
            }

            units[index.Name] = cost;
        }

        return units;
    }

    private static bool SameEntry(Dictionary<string, DynamoValue> a, Dictionary<string, DynamoValue> b) =>
        a.Count == b.Count && a.All(e => b.TryGetValue(e.Key, out var other) && e.Value.Equals(other));

    /// <summary>An item as an index holds it: the attributes its projection keeps.</summary>
    private static Dictionary<string, DynamoValue> ProjectForIndex(DynamoTable table, DynamoIndex index, IReadOnlyDictionary<string, DynamoValue> item)
    {
        if (index.Projection.Type == "ALL")
        {
            return item as Dictionary<string, DynamoValue> ?? new Dictionary<string, DynamoValue>(item.ToDictionary(e => e.Key, e => e.Value), StringComparer.Ordinal);
        }

        var result = new Dictionary<string, DynamoValue>(StringComparer.Ordinal);
        foreach (var entry in item)
        {
            if (IndexProjects(table, index, entry.Key))
            {
                result[entry.Key] = entry.Value;
            }
        }

        return result;
    }

    /// <summary>Whether an index projects an attribute.</summary>
    private static bool IndexProjects(DynamoTable table, DynamoIndex index, string attribute) =>
        index.Projection.Type == "ALL"
        || table.KeySchema.IsKeyAttribute(attribute)
        || index.KeySchema.IsKeyAttribute(attribute)
        || (index.Projection.Type == "INCLUDE" && index.Projection.NonKeyAttributes!.Contains(attribute, StringComparer.Ordinal));

    private static void CheckReturnConsumedCapacity(string? value)
    {
        // The model's enum check covers it; kept for requests built without the handler.
        _ = value;
    }

    private static List<KeySchemaElement> KeySchemaElements(DynamoKeySchema schema)
    {
        var elements = new List<KeySchemaElement> { new() { AttributeName = schema.Partition.Name, KeyType = "HASH" } };
        if (schema.Sort is not null)
        {
            elements.Add(new KeySchemaElement { AttributeName = schema.Sort.Name, KeyType = "RANGE" });
        }

        return elements;
    }

    private static Model.Projection ProjectionModel(DynamoProjection projection) => new()
    {
        ProjectionType = projection.Type,
        NonKeyAttributes = projection.NonKeyAttributes?.ToList(),
    };

    private static ProvisionedThroughputDescription ThroughputDescription(DynamoThroughput throughput) => new()
    {
        LastIncreaseDateTime = throughput.LastIncreaseDateTime,
        LastDecreaseDateTime = throughput.LastDecreaseDateTime,
        NumberOfDecreasesToday = throughput.NumberOfDecreasesToday,
        ReadCapacityUnits = throughput.ReadCapacityUnits,
        WriteCapacityUnits = throughput.WriteCapacityUnits,
    };

    /// <summary>The table as DescribeTable (and CreateTable, UpdateTable, DeleteTable) describes it.</summary>
    private static TableDescription Describe(DynamoTable table)
    {
        var onDemand = table.BillingMode == "PAY_PER_REQUEST";
        var description = new TableDescription
        {
            AttributeDefinitions = table.AttributeDefinitions.Select(a => new AttributeDefinition { AttributeName = a.Name, AttributeType = a.Type.ToString() }).ToList(),
            TableName = table.Name,
            KeySchema = KeySchemaElements(table.KeySchema),
            TableStatus = table.Status,
            CreationDateTime = table.CreationDateTime,
            ProvisionedThroughput = ThroughputDescription(table.Throughput),
            TableSizeBytes = table.SizeBytes,
            ItemCount = table.ItemCount,
            TableArn = table.Arn,
            TableId = table.TableId,
            DeletionProtectionEnabled = table.DeletionProtectionEnabled,
            WarmThroughput = new TableWarmThroughputDescription
            {
                ReadUnitsPerSecond = onDemand ? 12000 : Math.Max(12000, table.Throughput.ReadCapacityUnits),
                WriteUnitsPerSecond = onDemand ? 4000 : Math.Max(4000, table.Throughput.WriteCapacityUnits),
                Status = table.Status == "CREATING" ? "UPDATING" : "ACTIVE",
            },
        };

        if (table.BillingModeStated || onDemand)
        {
            description.BillingModeSummary = new BillingModeSummary
            {
                BillingMode = table.BillingMode,
                LastUpdateToPayPerRequestDateTime = onDemand ? table.LastUpdateToPayPerRequestDateTime : null,
            };
        }

        if (onDemand && (table.MaxReadRequestUnits is not null || table.MaxWriteRequestUnits is not null))
        {
            description.OnDemandThroughput = new OnDemandThroughput
            {
                MaxReadRequestUnits = table.MaxReadRequestUnits ?? -1,
                MaxWriteRequestUnits = table.MaxWriteRequestUnits ?? -1,
            };
        }

        if (table.LocalIndexes.Count > 0)
        {
            description.LocalSecondaryIndexes = table.LocalIndexes.Select(index => new LocalSecondaryIndexDescription
            {
                IndexName = index.Name,
                KeySchema = KeySchemaElements(index.KeySchema),
                Projection = ProjectionModel(index.Projection),
                IndexSizeBytes = index.SizeBytes,
                ItemCount = index.ItemCount,
                IndexArn = index.Arn,
            }).ToList();
        }

        if (table.GlobalIndexes.Count > 0)
        {
            description.GlobalSecondaryIndexes = table.GlobalIndexes.Select(index => new GlobalSecondaryIndexDescription
            {
                IndexName = index.Name,
                KeySchema = KeySchemaElements(index.KeySchema),
                Projection = ProjectionModel(index.Projection),
                IndexStatus = index.Status,
                Backfilling = index.Status == "CREATING" ? true : null,
                ProvisionedThroughput = ThroughputDescription(index.Throughput ?? new DynamoThroughput()),
                IndexSizeBytes = index.SizeBytes,
                ItemCount = index.ItemCount,
                IndexArn = index.Arn,
                OnDemandThroughput = onDemand && (index.MaxReadRequestUnits is not null || index.MaxWriteRequestUnits is not null)
                    ? new OnDemandThroughput { MaxReadRequestUnits = index.MaxReadRequestUnits ?? -1, MaxWriteRequestUnits = index.MaxWriteRequestUnits ?? -1 }
                    : null,
                WarmThroughput = new GlobalSecondaryIndexWarmThroughputDescription
                {
                    ReadUnitsPerSecond = onDemand ? 12000 : Math.Max(12000, index.Throughput?.ReadCapacityUnits ?? 0),
                    WriteUnitsPerSecond = onDemand ? 4000 : Math.Max(4000, index.Throughput?.WriteCapacityUnits ?? 0),
                    Status = index.Status == "CREATING" ? "UPDATING" : "ACTIVE",
                },
            }).ToList();
        }

        if (table.StreamEnabled)
        {
            description.StreamSpecification = new StreamSpecification { StreamEnabled = true, StreamViewType = table.StreamViewType };
        }

        if (table.LatestStream is not null)
        {
            description.LatestStreamLabel = table.LatestStream.Label;
            description.LatestStreamArn = table.LatestStream.Arn;
        }

        if (table.SseEnabled)
        {
            description.SSEDescription = new SSEDescription { Status = "ENABLED", SSEType = table.SseType ?? "KMS", KMSMasterKeyArn = table.SseKmsKeyArn };
        }

        if (table.Restore is { } restore)
        {
            description.RestoreSummary = new RestoreSummary
            {
                SourceBackupArn = restore.SourceBackupArn,
                SourceTableArn = restore.SourceTableArn,
                RestoreDateTime = restore.RestoreDateTime,
                RestoreInProgress = false,
            };
        }

        if (table.TableClass is not null)
        {
            description.TableClassSummary = new TableClassSummary { TableClass = table.TableClass, LastUpdateDateTime = table.TableClassLastUpdate };
        }

        return description;
    }
}

using LocalSqsSnsMessaging.DynamoDb.Model;

namespace LocalSqsSnsMessaging.DynamoDb;

/// <summary>Table operations: CreateTable, DescribeTable, ListTables, UpdateTable, DeleteTable and tags.</summary>
internal sealed partial class InternalDynamoDbClient
{
    private const int MaxLocalIndexes = 5;
    private const int MaxGlobalIndexes = 20;
    private const int MaxProjectedAttributes = 100;
    private const int MaxTags = 50;

    public Task<CreateTableResponse> CreateTableAsync(CreateTableRequest request, CancellationToken cancellationToken = default)
    {
        var name = TableName(request.TableName!);
        CheckTableName(name);
        if (request.KeySchema is null)
        {
            throw DynamoDbErrors.ValidationClause(AwsValidation.Missing("keySchema"));
        }

        if (request.AttributeDefinitions is null)
        {
            throw DynamoDbErrors.ValidationClause(AwsValidation.Missing("attributeDefinitions"));
        }

        var definitions = new List<DynamoKeyAttribute>();
        foreach (var definition in request.AttributeDefinitions)
        {
            if (definitions.Any(d => string.Equals(d.Name, definition.AttributeName, StringComparison.Ordinal)))
            {
                throw DynamoDbErrors.InvalidParameter($"Duplicate AttributeName in AttributeDefinitions: {definition.AttributeName}");
            }

            definitions.Add(new DynamoKeyAttribute(definition.AttributeName!, ParseScalarType(definition.AttributeType)));
        }

        var keyAttributes = new HashSet<string>(
            request.KeySchema
                .Concat(request.LocalSecondaryIndexes?.SelectMany(i => i.KeySchema ?? []) ?? [])
                .Concat(request.GlobalSecondaryIndexes?.SelectMany(i => i.KeySchema ?? []) ?? [])
                .Select(e => e.AttributeName!),
            StringComparer.Ordinal);
        var keySchema = ParseKeySchema(request.KeySchema, definitions, indexName: null, keyAttributes);
        if (request.StreamSpecification is { StreamEnabled: not true, StreamViewType: not null })
        {
            throw DynamoDbErrors.InvalidParameter("Table is being created with a stream disabled, UpdateViewType should not be specified");
        }

        var billingMode = request.BillingMode ?? "PROVISIONED";
        var onDemand = billingMode == "PAY_PER_REQUEST";
        CheckThroughput(request.ProvisionedThroughput, onDemand, indexName: null);
        CheckOnDemandThroughput(billingMode, request.OnDemandThroughput, request.GlobalSecondaryIndexes?.Select(i => i.OnDemandThroughput));
        CheckTableClass(request.TableClass);

        var arn = TableArn(name);
        var table = new DynamoTable
        {
            Name = name,
            Arn = arn,
            TableId = Guid.NewGuid().ToString(),
            CreationDateTime = Now,
            KeySchema = keySchema,
            AttributeDefinitions = definitions,
            BillingMode = billingMode,
            BillingModeStated = request.BillingMode is not null,
            LastUpdateToPayPerRequestDateTime = onDemand ? Now : null,
            Throughput = new DynamoThroughput
            {
                ReadCapacityUnits = request.ProvisionedThroughput?.ReadCapacityUnits ?? 0,
                WriteCapacityUnits = request.ProvisionedThroughput?.WriteCapacityUnits ?? 0,
            },
            MaxReadRequestUnits = request.OnDemandThroughput?.MaxReadRequestUnits,
            MaxWriteRequestUnits = request.OnDemandThroughput?.MaxWriteRequestUnits,
            DeletionProtectionEnabled = request.DeletionProtectionEnabled ?? false,
            TableClass = request.TableClass,
            TableClassLastUpdate = null,
            ResourcePolicy = request.ResourcePolicy,
        };

        var used = new HashSet<string>(keySchema.Attributes.Select(a => a.Name), StringComparer.Ordinal);
        var indexNames = new HashSet<string>(StringComparer.Ordinal);
        if (request.LocalSecondaryIndexes is { } localIndexes)
        {
            if (keySchema.Sort is null)
            {
                throw DynamoDbErrors.InvalidParameter("Table KeySchema does not have a range key, which is required when specifying a LocalSecondaryIndex");
            }

            if (localIndexes.Count > MaxLocalIndexes)
            {
                throw DynamoDbErrors.InvalidParameter($"Number of LocalSecondaryIndexes exceeds per-table limit of {MaxLocalIndexes}");
            }

            foreach (var local in localIndexes)
            {
                var indexName = local.IndexName!;
                if (!indexNames.Add(indexName))
                {
                    throw DynamoDbErrors.InvalidParameter($"Duplicate index name: {indexName}");
                }

                var indexKey = ParseKeySchema(local.KeySchema ?? [], definitions, indexName);
                if (!string.Equals(indexKey.Partition.Name, keySchema.Partition.Name, StringComparison.Ordinal))
                {
                    throw DynamoDbErrors.InvalidParameter($"Index KeySchema does not have the same leading hash key as table KeySchema for index: {indexName}. index hash key: {indexKey.Partition.Name}, table hash key: {keySchema.Partition.Name}");
                }

                if (indexKey.Sort is null)
                {
                    throw DynamoDbErrors.InvalidParameter($"Index KeySchema does not have a range key for index: {indexName}");
                }

                used.UnionWith(indexKey.Attributes.Select(a => a.Name));
                table.LocalIndexes.Add(new DynamoIndex
                {
                    Name = indexName,
                    Arn = $"{arn}/index/{indexName}",
                    IsGlobal = false,
                    KeySchema = indexKey,
                    Projection = ParseProjection(local.Projection),
                });
            }
        }

        if (request.GlobalSecondaryIndexes is { } globalIndexes)
        {
            if (globalIndexes.Count > MaxGlobalIndexes)
            {
                throw DynamoDbErrors.LimitExceeded($"Subscriber limit exceeded: Number of global secondary indexes exceeds per-table limit of {MaxGlobalIndexes}");
            }

            foreach (var global in globalIndexes)
            {
                var indexName = global.IndexName!;
                if (!indexNames.Add(indexName))
                {
                    throw DynamoDbErrors.InvalidParameter($"Duplicate index name: {indexName}");
                }

                var indexKey = ParseKeySchema(global.KeySchema ?? [], definitions, indexName);
                CheckThroughput(global.ProvisionedThroughput, onDemand, indexName);
                used.UnionWith(indexKey.Attributes.Select(a => a.Name));
                table.GlobalIndexes.Add(new DynamoIndex
                {
                    Name = indexName,
                    Arn = $"{arn}/index/{indexName}",
                    IsGlobal = true,
                    KeySchema = indexKey,
                    Projection = ParseProjection(global.Projection),
                    Throughput = new DynamoThroughput
                    {
                        ReadCapacityUnits = global.ProvisionedThroughput?.ReadCapacityUnits ?? 0,
                        WriteCapacityUnits = global.ProvisionedThroughput?.WriteCapacityUnits ?? 0,
                    },
                    MaxReadRequestUnits = global.OnDemandThroughput?.MaxReadRequestUnits,
                    MaxWriteRequestUnits = global.OnDemandThroughput?.MaxWriteRequestUnits,
                });
            }
        }

        // The 100-attribute projection budget is the table's, and a name projected into two indexes counts twice.
        var projected = table.LocalIndexes.Concat(table.GlobalIndexes).Sum(i => i.Projection.NonKeyAttributes?.Count ?? 0);
        if (projected > MaxProjectedAttributes)
        {
            throw DynamoDbErrors.InvalidParameter($"Number of projected attributes in all indexes exceeds limit of {MaxProjectedAttributes}, number of projected attributes:{projected.ToString(CultureInfo.InvariantCulture)}");
        }

        if (definitions.Count != used.Count || definitions.Any(d => !used.Contains(d.Name)))
        {
            throw DynamoDbErrors.InvalidParameter("Number of attributes in KeySchema does not exactly match number of attributes defined in AttributeDefinitions");
        }

        if (request.StreamSpecification is { } stream)
        {
            if (stream.StreamEnabled == true && stream.StreamViewType is null)
            {
                throw DynamoDbErrors.InvalidParameter("Table is being created with a stream enabled, but StreamViewType is not specified");
            }

            table.StreamEnabled = stream.StreamEnabled == true;
            table.StreamViewType = table.StreamEnabled ? stream.StreamViewType : null;
        }

        if (request.SSESpecification is { Enabled: true } sse)
        {
            table.SseEnabled = true;
            table.SseType = sse.SSEType ?? "KMS";
            table.SseKmsKeyArn = sse.KMSMasterKeyId is { } key && key.StartsWith("arn:", StringComparison.Ordinal)
                ? key
                : $"arn:aws:kms:{Region}:{_bus.CurrentAccountId}:key/{(sse.KMSMasterKeyId is null ? "aws/dynamodb" : sse.KMSMasterKeyId)}";
        }

        if (request.Tags is { } tags)
        {
            if (tags.Count > MaxTags)
            {
                throw DynamoDbErrors.LimitExceeded("Number of Tags exceed the current limit for the provided TableName");
            }

            foreach (var tag in tags)
            {
                SetTag(table, tag.Key!, tag.Value ?? string.Empty);
            }
        }

        lock (Store.Gate)
        {
            if (Store.Tables.ContainsKey(name))
            {
                throw DynamoDbErrors.ResourceInUse($"Table already exists: {name}");
            }

            if (_bus.DynamoDbTableCreationDelay > TimeSpan.Zero)
            {
                table.Status = "CREATING";
                table.ActiveAt = _bus.TimeProvider.GetUtcNow() + _bus.DynamoDbTableCreationDelay;
                foreach (var index in table.GlobalIndexes)
                {
                    index.Status = "CREATING";
                }
            }

            if (table.StreamEnabled)
            {
                StartStream(table);
            }

            Store.Tables[name] = table;
            Record("CreateTable", arn);
            return Task.FromResult(new CreateTableResponse { TableDescription = Describe(table) });
        }
    }

    private static DynamoType ParseScalarType(string? type) => type switch
    {
        "S" => DynamoType.S,
        "N" => DynamoType.N,
        "B" => DynamoType.B,
        null => throw DynamoDbErrors.ValidationClause(AwsValidation.Missing("attributeDefinitions.member.attributeType")),
        _ => throw DynamoDbErrors.ValidationClause(AwsValidation.Clause(type, "attributeDefinitions.member.attributeType", "Member must satisfy enum value set: [B, N, S]")),
    };

    /// <summary>A key schema: a HASH key first, then at most a RANGE key, each with a defined type.</summary>
    /// <param name="elements">The key schema as the request states it.</param>
    /// <param name="definitions">The attribute definitions its keys resolve against.</param>
    /// <param name="indexName">The index the schema keys; null for the table's own key.</param>
    /// <param name="keyAttributes">
    /// For the table's own key, every attribute some key schema of the request names: a table key
    /// with no definition, when every definition is used, gets DynamoDB's fixed sentence instead
    /// of the two lists.
    /// </param>
    private static DynamoKeySchema ParseKeySchema(List<KeySchemaElement> elements, List<DynamoKeyAttribute> definitions, string? indexName, HashSet<string>? keyAttributes = null)
    {
        if (elements.Count == 0 || elements.Count > 2)
        {
            throw DynamoDbErrors.ValidationClause(AwsValidation.Clause(JavaRendering.List(elements, e => e.ToString()), indexName is null ? "keySchema" : "globalSecondaryIndexes.member.keySchema", elements.Count == 0 ? "Member must have length greater than or equal to 1" : "Member must have length less than or equal to 2"));
        }

        if (elements[0].KeyType != "HASH")
        {
            throw DynamoDbErrors.Validation("Invalid KeySchema: The first KeySchemaElement is not a HASH key type");
        }

        if (elements.Count == 2 && elements[1].KeyType != "RANGE")
        {
            throw DynamoDbErrors.Validation("Invalid KeySchema: The second KeySchemaElement is not a RANGE key type");
        }

        var undefined = elements.Where(e => !definitions.Any(d => string.Equals(d.Name, e.AttributeName, StringComparison.Ordinal))).ToList();
        if (undefined.Count > 0)
        {
            if (indexName is null && keyAttributes is not null && definitions.All(d => keyAttributes.Contains(d.Name)))
            {
                throw DynamoDbErrors.Validation(KeyWithoutDefinition);
            }

            throw UndefinedIndexKeys(elements, definitions.Select(d => d.Name));
        }

        // A repeated key attribute leaves an element without a definition of its own, unless the
        // request declared as many definitions as the table key has elements.
        if (elements.Count == 2 && string.Equals(elements[0].AttributeName, elements[1].AttributeName, StringComparison.Ordinal))
        {
            throw DynamoDbErrors.Validation(indexName is null && definitions.Count >= elements.Count
                ? "Both the Hash Key and the Range Key element in the KeySchema have the same name"
                : KeyWithoutDefinition);
        }

        DynamoKeyAttribute Attribute(KeySchemaElement element) =>
            definitions.First(d => string.Equals(d.Name, element.AttributeName, StringComparison.Ordinal));

        return new DynamoKeySchema(Attribute(elements[0]), elements.Count == 2 ? Attribute(elements[1]) : null);
    }

    private const string KeyWithoutDefinition = "Invalid KeySchema: Some index key attribute have no definition";

    /// <summary>The refusal of a key schema naming an attribute the definitions don't: the whole key schema and every definition, each in DynamoDB's hash-table order.</summary>
    private static DynamoDbException UndefinedIndexKeys(IEnumerable<KeySchemaElement> elements, IEnumerable<string> definitions) =>
        DynamoDbErrors.InvalidParameter(
            $"Some index key attributes are not defined in AttributeDefinitions. Keys: [{string.Join(", ", HashTableOrder(elements.Select(e => e.AttributeName!)))}], AttributeDefinitions: [{string.Join(", ", HashTableOrder(definitions))}]");

    /// <summary>
    /// Names in the order DynamoDB lists them in that refusal: out of a Java <c>HashSet</c>, so by
    /// slot (the name's <c>String.hashCode</c> spread by its high half, modulo a power-of-two slot
    /// count sized for a 3/4 load factor), and names of one slot in the order they came.
    /// </summary>
    private static List<string> HashTableOrder(IEnumerable<string> names)
    {
        var distinct = names.Distinct(StringComparer.Ordinal).ToList();
        var slots = 2;
        while (distinct.Count * 4 > slots * 3)
        {
            slots *= 2;
        }

        int Slot(string name)
        {
            var hash = 0;
            foreach (var c in name)
            {
                hash = unchecked((hash * 31) + c);
            }

            var spread = (uint)hash ^ ((uint)hash >> 16);
            return (int)(spread & (uint)(slots - 1));
        }

        return distinct.Select((name, position) => (name, position)).OrderBy(e => Slot(e.name)).ThenBy(e => e.position).Select(e => e.name).ToList();
    }

    private static DynamoProjection ParseProjection(Model.Projection? projection)
    {
        var type = projection?.ProjectionType ?? "ALL";
        var nonKey = projection?.NonKeyAttributes;
        switch (type)
        {
            case "INCLUDE" when nonKey is null || nonKey.Count == 0:
                throw DynamoDbErrors.InvalidParameter("ProjectionType is INCLUDE, but NonKeyAttributes is not specified");
            case "KEYS_ONLY" when nonKey is { Count: > 0 }:
                throw DynamoDbErrors.InvalidParameter("ProjectionType is KEYS_ONLY, but NonKeyAttributes is specified");
            case "ALL" when nonKey is { Count: > 0 }:
                throw DynamoDbErrors.InvalidParameter("ProjectionType is ALL, but NonKeyAttributes is specified");
        }

        if (nonKey is not null)
        {
            var duplicate = nonKey.GroupBy(n => n, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
            if (duplicate is not null)
            {
                throw DynamoDbErrors.InvalidParameter($"Duplicate value in attribute name: {duplicate.Key}");
            }
        }

        return new DynamoProjection(type, type == "INCLUDE" ? nonKey!.ToList() : null);
    }

    private static void CheckThroughput(ProvisionedThroughput? throughput, bool onDemand, string? indexName)
    {
        if (onDemand && throughput is not null)
        {
            throw indexName is null
                ? DynamoDbErrors.InvalidParameter("Neither ReadCapacityUnits nor WriteCapacityUnits can be specified when BillingMode is PAY_PER_REQUEST")
                : DynamoDbErrors.InvalidParameter($"ProvisionedThroughput should not be specified for index: {indexName} when BillingMode is PAY_PER_REQUEST");
        }

        if (!onDemand && (throughput?.ReadCapacityUnits is null || throughput.WriteCapacityUnits is null))
        {
            throw indexName is null
                ? DynamoDbErrors.InvalidParameter("ReadCapacityUnits and WriteCapacityUnits must both be specified when BillingMode is PROVISIONED")
                : DynamoDbErrors.InvalidParameter($"Both ReadCapacityUnits and WriteCapacityUnits must be specified for index: {indexName}");
        }
    }

    /// <summary>
    /// On-demand ceilings: only an on-demand table has them, and a table's ceiling is at least 1
    /// (or -1, which removes it).
    /// </summary>
    private static void CheckOnDemandThroughput(string billingMode, OnDemandThroughput? table, IEnumerable<OnDemandThroughput?>? indexes = null)
    {
        if (billingMode == "PROVISIONED")
        {
            foreach (var ceiling in (indexes ?? []).Prepend(table))
            {
                var member = ceiling?.MaxReadRequestUnits is not null ? "MaxReadRequestUnits"
                    : ceiling?.MaxWriteRequestUnits is not null ? "MaxWriteRequestUnits"
                    : null;
                if (member is not null)
                {
                    throw DynamoDbErrors.InvalidParameter($"{member} for OnDemandThroughput cannot be specified when the table BillingMode is PROVISIONED");
                }
            }

            return;
        }

        if (table?.MaxReadRequestUnits is { } read && read != -1 && read < 1)
        {
            throw DynamoDbErrors.InvalidParameter("Requested MaxReadRequestUnits for OnDemandThroughput for table is outside of valid range");
        }

        if (table?.MaxWriteRequestUnits is { } write && write != -1 && write < 1)
        {
            throw DynamoDbErrors.InvalidParameter("Requested MaxWriteRequestUnits for OnDemandThroughput for table is outside of valid range");
        }
    }

    /// <summary>DynamoDB checks <c>TableClass</c> itself, with its own sentence rather than the model's enum clause.</summary>
    private static void CheckTableClass(string? tableClass)
    {
        if (tableClass is not null and not "STANDARD" and not "STANDARD_INFREQUENT_ACCESS")
        {
            throw DynamoDbErrors.Validation("Invalid table-class parameter provided. Please try again with a valid table-class value: [STANDARD, STANDARD_INFREQUENT_ACCESS].");
        }
    }

    public Task<DescribeTableResponse> DescribeTableAsync(DescribeTableRequest request, CancellationToken cancellationToken = default)
    {
        lock (Store.Gate)
        {
            var table = ExistingTable(request.TableName!);
            Record("DescribeTable", table.Arn);
            return Task.FromResult(new DescribeTableResponse { Table = Describe(table) });
        }
    }

    public Task<ListTablesResponse> ListTablesAsync(ListTablesRequest request, CancellationToken cancellationToken = default)
    {
        lock (Store.Gate)
        {
            var limit = request.Limit ?? 100;
            var names = Store.Tables.Keys
                .OrderBy(n => n, StringComparer.Ordinal)
                .Where(n => request.ExclusiveStartTableName is null || string.CompareOrdinal(n, request.ExclusiveStartTableName) > 0)
                .ToList();
            var page = names.Take(limit).ToList();
            Record("ListTables");
            return Task.FromResult(new ListTablesResponse
            {
                TableNames = page,
                LastEvaluatedTableName = names.Count > limit ? page[^1] : null,
            });
        }
    }

    public Task<DeleteTableResponse> DeleteTableAsync(DeleteTableRequest request, CancellationToken cancellationToken = default)
    {
        lock (Store.Gate)
        {
            var table = ExistingTable(request.TableName!);
            if (table.Status == "DELETING")
            {
                throw DynamoDbErrors.ResourceInUse($"Attempt to change a resource which is still in use: Table is being deleted: {table.Name}");
            }

            if (table.DeletionProtectionEnabled)
            {
                throw DynamoDbErrors.Validation("Resource cannot be deleted as it is currently protected against deletion. Disable deletion protection first.");
            }

            table.Status = "DELETING";
            var description = Describe(table);

            // The deleting table's answer leaves its global indexes out.
            description.GlobalSecondaryIndexes = null;
            Store.Tables.Remove(table.Name);
            if (table.LatestStream is { } stream)
            {
                stream.Disable(Now);
            }

            Store.TimeToLiveJobs.Remove(table.Name);
            Record("DeleteTable", table.Arn);
            return Task.FromResult(new DeleteTableResponse { TableDescription = description });
        }
    }

    public Task<UpdateTableResponse> UpdateTableAsync(UpdateTableRequest request, CancellationToken cancellationToken = default)
    {
        CheckIndexUpdates(request.GlobalSecondaryIndexUpdates);
        CheckTableClass(request.TableClass);
        lock (Store.Gate)
        {
            var table = ExistingTable(request.TableName!);
            if (table.Status != "ACTIVE")
            {
                throw DynamoDbErrors.ResourceInUse($"Attempt to change a resource which is still in use: Table is being {(table.Status == "CREATING" ? "created" : "updated")}: {table.Name}");
            }

            var changed = false;
            if (request.BillingMode is { } billingMode)
            {
                var onDemand = billingMode == "PAY_PER_REQUEST";
                if (onDemand && request.ProvisionedThroughput is not null)
                {
                    throw DynamoDbErrors.InvalidParameter("ProvisionedThroughput cannot be specified when BillingMode is PAY_PER_REQUEST");
                }

                if (!onDemand && table.BillingMode == "PAY_PER_REQUEST" && request.ProvisionedThroughput is null)
                {
                    throw DynamoDbErrors.InvalidParameter("ProvisionedThroughput must be specified when BillingMode is PROVISIONED");
                }

                if (billingMode != table.BillingMode)
                {
                    table.BillingMode = billingMode;
                    if (onDemand)
                    {
                        table.LastUpdateToPayPerRequestDateTime = Now;
                        table.Throughput = new DynamoThroughput();
                        foreach (var index in table.GlobalIndexes)
                        {
                            index.Throughput = new DynamoThroughput();
                        }
                    }
                }

                table.BillingModeStated = true;
                changed = true;
            }

            if (request.ProvisionedThroughput is { } throughput)
            {
                if (table.BillingMode == "PAY_PER_REQUEST")
                {
                    throw DynamoDbErrors.InvalidParameter("Neither ReadCapacityUnits nor WriteCapacityUnits can be specified when BillingMode is PAY_PER_REQUEST");
                }

                var read = throughput.ReadCapacityUnits ?? table.Throughput.ReadCapacityUnits;
                var write = throughput.WriteCapacityUnits ?? table.Throughput.WriteCapacityUnits;
                if (read == table.Throughput.ReadCapacityUnits && write == table.Throughput.WriteCapacityUnits && table.BillingMode == "PROVISIONED" && request.BillingMode is null or "PROVISIONED")
                {
                    throw DynamoDbErrors.Validation($"The provisioned throughput for the table will not change. The requested value equals the current value. Current ReadCapacityUnits provisioned for the table: {table.Throughput.ReadCapacityUnits}. Requested ReadCapacityUnits: {read}. Current WriteCapacityUnits provisioned for the table: {table.Throughput.WriteCapacityUnits}. Requested WriteCapacityUnits: {write}. Refer to the Amazon DynamoDB Developer Guide for current limits and how to request higher limits.");
                }

                if (read > table.Throughput.ReadCapacityUnits || write > table.Throughput.WriteCapacityUnits)
                {
                    table.Throughput.LastIncreaseDateTime = Now;
                }

                if (read < table.Throughput.ReadCapacityUnits || write < table.Throughput.WriteCapacityUnits)
                {
                    table.Throughput.LastDecreaseDateTime = Now;
                    table.Throughput.NumberOfDecreasesToday++;
                }

                table.Throughput.ReadCapacityUnits = read;
                table.Throughput.WriteCapacityUnits = write;
                changed = true;
            }

            CheckOnDemandThroughput(request.BillingMode ?? table.BillingMode, request.OnDemandThroughput);
            if (request.OnDemandThroughput is { } onDemandThroughput)
            {
                table.MaxReadRequestUnits = onDemandThroughput.MaxReadRequestUnits ?? table.MaxReadRequestUnits;
                table.MaxWriteRequestUnits = onDemandThroughput.MaxWriteRequestUnits ?? table.MaxWriteRequestUnits;
                changed = true;
            }

            if (request.AttributeDefinitions is { } definitions)
            {
                foreach (var definition in definitions)
                {
                    var type = ParseScalarType(definition.AttributeType);
                    var existing = table.DeclaredType(definition.AttributeName!);
                    if (existing is not null && existing != type)
                    {
                        throw DynamoDbErrors.InvalidParameter($"Conflicting attribute definition for {definition.AttributeName}: {existing} and {type}");
                    }
                }
            }

            if (request.GlobalSecondaryIndexUpdates is { Count: > 0 } updates)
            {
                foreach (var update in updates)
                {
                    ApplyIndexUpdate(table, update, request.AttributeDefinitions);
                }

                changed = true;
            }

            if (request.StreamSpecification is { } stream)
            {
                if (stream.StreamEnabled == true)
                {
                    if (table.StreamEnabled)
                    {
                        throw DynamoDbErrors.Validation("Table already has an enabled stream: TableName: " + table.Name);
                    }

                    if (stream.StreamViewType is null)
                    {
                        throw DynamoDbErrors.InvalidParameter("Table is being updated with a stream enabled, but StreamViewType is not specified");
                    }

                    table.StreamEnabled = true;
                    table.StreamViewType = stream.StreamViewType;
                    StartStream(table);
                }
                else
                {
                    if (!table.StreamEnabled)
                    {
                        throw DynamoDbErrors.Validation("Table has no stream to disable: TableName: " + table.Name);
                    }

                    table.StreamEnabled = false;
                    table.StreamViewType = null;
                    table.LatestStream?.Disable(Now);
                }

                changed = true;
            }

            if (request.SSESpecification is { } sse)
            {
                table.SseEnabled = sse.Enabled ?? true;
                table.SseType = sse.SSEType ?? "KMS";
                table.SseKmsKeyArn = table.SseEnabled ? $"arn:aws:kms:{Region}:{_bus.CurrentAccountId}:key/{sse.KMSMasterKeyId ?? "aws/dynamodb"}" : null;
                changed = true;
            }

            if (request.TableClass is { } tableClass)
            {
                // DynamoDB allows two table class changes in a trailing 30 days.
                table.TableClassUpdates.RemoveAll(at => at <= Now.AddDays(-30));
                if (table.TableClassUpdates.Count >= 2)
                {
                    throw DynamoDbErrors.LimitExceeded("Subscriber limit exceeded: Updates to TableClass are limited to 2 times in 30 day(s).");
                }

                table.TableClassUpdates.Add(Now);
                table.TableClass = tableClass;
                table.TableClassLastUpdate = Now;
                changed = true;
            }

            if (request.DeletionProtectionEnabled is { } protection)
            {
                table.DeletionProtectionEnabled = protection;
                changed = true;
            }

            if (request.WarmThroughput is not null)
            {
                changed = true;
            }

            if (!changed)
            {
                throw DynamoDbErrors.Validation("At least one of ProvisionedThroughput, BillingMode, UpdateStreamEnabled, GlobalSecondaryIndexUpdates or SSESpecification or ReplicaUpdates is required");
            }

            Record("UpdateTable", table.Arn);
            return Task.FromResult(new UpdateTableResponse { TableDescription = Describe(table) });
        }
    }

    private static void ApplyIndexUpdate(DynamoTable table, GlobalSecondaryIndexUpdate update, List<AttributeDefinition>? definitions)
    {
        if (update.Create is { } create)
        {
            var indexName = create.IndexName!;
            if (table.FindIndex(indexName) is not null)
            {
                throw DynamoDbErrors.Validation("Attempting to create an index which already exists");
            }

            if (table.GlobalIndexes.Count >= MaxGlobalIndexes)
            {
                throw DynamoDbErrors.LimitExceeded($"Subscriber limit exceeded: Number of global secondary indexes exceeds per-table limit of {MaxGlobalIndexes}");
            }

            // The new index's keys must be declared by this request's AttributeDefinitions.
            var keyElements = create.KeySchema ?? [];
            if (!keyElements.All(e => definitions?.Any(d => string.Equals(d.AttributeName, e.AttributeName, StringComparison.Ordinal)) == true))
            {
                if (definitions is not { Count: > 0 })
                {
                    throw DynamoDbErrors.InvalidParameter($"AttributeDefinitions is not specified for index: {indexName}");
                }

                throw UndefinedIndexKeys(keyElements, definitions.Select(d => d.AttributeName!));
            }

            var known = table.AttributeDefinitions.ToList();
            foreach (var definition in definitions ?? [])
            {
                var type = ParseScalarType(definition.AttributeType);
                if (!known.Any(k => string.Equals(k.Name, definition.AttributeName, StringComparison.Ordinal)))
                {
                    known.Add(new DynamoKeyAttribute(definition.AttributeName!, type));
                }
            }

            var keySchema = ParseKeySchema(create.KeySchema ?? [], known, indexName);
            CheckThroughput(create.ProvisionedThroughput, table.BillingMode == "PAY_PER_REQUEST", indexName);
            foreach (var attribute in keySchema.Attributes)
            {
                if (table.DeclaredType(attribute.Name) is null)
                {
                    table.AttributeDefinitions.Add(attribute);
                }
            }

            var index = new DynamoIndex
            {
                Name = indexName,
                Arn = $"{table.Arn}/index/{indexName}",
                IsGlobal = true,
                KeySchema = keySchema,
                Projection = ParseProjection(create.Projection),
                Throughput = new DynamoThroughput
                {
                    ReadCapacityUnits = create.ProvisionedThroughput?.ReadCapacityUnits ?? 0,
                    WriteCapacityUnits = create.ProvisionedThroughput?.WriteCapacityUnits ?? 0,
                },
                MaxReadRequestUnits = create.OnDemandThroughput?.MaxReadRequestUnits,
                MaxWriteRequestUnits = create.OnDemandThroughput?.MaxWriteRequestUnits,
            };
            CheckExistingItemsFitIndex(table, index);
            table.GlobalIndexes.Add(index);
            table.Backfill(index);
        }
        else if (update.Delete is { } delete)
        {
            var index = table.FindIndex(delete.IndexName!);
            if (index is null)
            {
                throw DynamoDbErrors.ResourceNotFound($"Requested resource not found: Index {delete.IndexName} for table {table.Name}");
            }

            if (!index.IsGlobal)
            {
                throw DynamoDbErrors.Validation($"Cannot delete an index that is not a GlobalSecondaryIndex: {index.Name}");
            }

            table.GlobalIndexes.Remove(index);
            var stillUsed = new HashSet<string>(table.KeySchema.Attributes.Concat(table.Indexes.SelectMany(i => i.KeySchema.Attributes)).Select(a => a.Name), StringComparer.Ordinal);
            table.AttributeDefinitions.RemoveAll(a => !stillUsed.Contains(a.Name));
        }
        else if (update.Update is { } change)
        {
            var index = table.FindIndex(change.IndexName!) as DynamoIndex;
            if (index is null || !index.IsGlobal)
            {
                throw DynamoDbErrors.ResourceNotFound($"Requested resource not found: Index: {change.IndexName} not found");
            }

            if (change.ProvisionedThroughput is { } throughput)
            {
                index.Throughput ??= new DynamoThroughput();
                index.Throughput.ReadCapacityUnits = throughput.ReadCapacityUnits ?? index.Throughput.ReadCapacityUnits;
                index.Throughput.WriteCapacityUnits = throughput.WriteCapacityUnits ?? index.Throughput.WriteCapacityUnits;
            }

            if (change.OnDemandThroughput is { } onDemand)
            {
                index.MaxReadRequestUnits = onDemand.MaxReadRequestUnits ?? index.MaxReadRequestUnits;
                index.MaxWriteRequestUnits = onDemand.MaxWriteRequestUnits ?? index.MaxWriteRequestUnits;
            }
        }
    }

    /// <summary>
    /// The index updates as a list: one update per index, and (an online index build being one
    /// at a time) at most one index created or deleted.
    /// </summary>
    private static void CheckIndexUpdates(List<GlobalSecondaryIndexUpdate>? updates)
    {
        if (updates is null)
        {
            return;
        }

        var named = new HashSet<string>(StringComparer.Ordinal);
        foreach (var update in updates)
        {
            var name = update.Create?.IndexName ?? update.Update?.IndexName ?? update.Delete?.IndexName;
            if (name is not null && !named.Add(name))
            {
                throw DynamoDbErrors.InvalidParameter($"Only one global secondary index update per index is allowed simultaneously. Index: {name}");
            }
        }

        if (updates.Count(u => u.Create is not null || u.Delete is not null) > 1)
        {
            throw DynamoDbErrors.LimitExceeded("Subscriber limit exceeded: Only 1 online index can be created or deleted simultaneously per table");
        }
    }

    /// <summary>A new global index whose key attribute an existing item holds with another type can't be built.</summary>
    private static void CheckExistingItemsFitIndex(DynamoTable table, DynamoIndex index)
    {
        foreach (var (_, item) in table.All())
        {
            foreach (var attribute in index.KeySchema.Attributes)
            {
                if (item.TryGetValue(attribute.Name, out var value) && value.Type != attribute.Type)
                {
                    // DynamoDB builds the index anyway and leaves such items out of it.
                    break;
                }
            }
        }
    }

    // Tags

    private static void SetTag(DynamoTable table, string key, string value) => SetTag(table.Tags, key, value);

    private static void SetTag(List<KeyValuePair<string, string>> tags, string key, string value)
    {
        var at = tags.FindIndex(t => string.Equals(t.Key, key, StringComparison.Ordinal));
        if (at >= 0)
        {
            tags[at] = new KeyValuePair<string, string>(key, value);
        }
        else
        {
            tags.Add(new KeyValuePair<string, string>(key, value));
        }
    }

    /// <summary>The table a resource ARN names, for the tagging operations.</summary>
    private DynamoTable TableForResource(string? resourceArn)
    {
        if (!Arn.TryParse(resourceArn, out var arn) || !string.Equals(arn.Service, "dynamodb", StringComparison.Ordinal) || !arn.Resource.StartsWith("table/", StringComparison.Ordinal))
        {
            throw DynamoDbErrors.Validation($"Invalid TableArn: Invalid ResourceArn provided as input {resourceArn}");
        }

        var name = arn.Resource["table/".Length..];
        if (name.Contains('/', StringComparison.Ordinal))
        {
            throw DynamoDbErrors.Validation($"Invalid TableArn: Invalid ResourceArn provided as input {resourceArn}");
        }

        if (!string.Equals(arn.Region, Region, StringComparison.Ordinal)
            || !string.Equals(arn.Account, _bus.CurrentAccountId, StringComparison.Ordinal)
            || !Store.Tables.TryGetValue(name, out var table))
        {
            throw DynamoDbErrors.ResourceNotFound($"Requested resource not found: ResourcArn: {resourceArn} not found");
        }

        return table;
    }

    /// <summary>
    /// The tags of the resource a tagging operation names: a table, or one of its streams, which
    /// has tags of its own.
    /// </summary>
    private List<KeyValuePair<string, string>> TagsOfResource(string? resourceArn)
    {
        var at = resourceArn?.IndexOf("/stream/", StringComparison.Ordinal) ?? -1;
        if (at < 0)
        {
            return TableForResource(resourceArn).Tags;
        }

        _ = TableForResource(resourceArn![..at]);
        return Store.Streams.TryGetValue(resourceArn, out var stream)
            ? stream.Tags
            : throw DynamoDbErrors.ResourceNotFound($"Requested resource not found: ResourceArn: {resourceArn} not found");
    }

    public Task<TagResourceResponse> TagResourceAsync(TagResourceRequest request, CancellationToken cancellationToken = default)
    {
        lock (Store.Gate)
        {
            var resourceTags = TagsOfResource(request.ResourceArn);
            var tags = request.Tags ?? [];
            if (tags.Count > MaxTags)
            {
                throw DynamoDbErrors.Validation("Number of Tags exceed the current limit for the provided ResourceArn");
            }

            var newKeys = tags.Select(t => t.Key!).Where(k => !resourceTags.Any(t => string.Equals(t.Key, k, StringComparison.Ordinal))).Distinct(StringComparer.Ordinal).Count();
            if (resourceTags.Count + newKeys > MaxTags)
            {
                throw DynamoDbErrors.LimitExceeded("Number of Tags exceed the current limit for the provided ResourceArn");
            }

            foreach (var tag in tags)
            {
                SetTag(resourceTags, tag.Key!, tag.Value ?? string.Empty);
            }

            Record("TagResource", request.ResourceArn);
            return Task.FromResult(new TagResourceResponse());
        }
    }

    public Task<UntagResourceResponse> UntagResourceAsync(UntagResourceRequest request, CancellationToken cancellationToken = default)
    {
        lock (Store.Gate)
        {
            var resourceTags = TagsOfResource(request.ResourceArn);
            var keys = new HashSet<string>(request.TagKeys ?? [], StringComparer.Ordinal);
            resourceTags.RemoveAll(t => keys.Contains(t.Key));
            Record("UntagResource", request.ResourceArn);
            return Task.FromResult(new UntagResourceResponse());
        }
    }

    public Task<ListTagsOfResourceResponse> ListTagsOfResourceAsync(ListTagsOfResourceRequest request, CancellationToken cancellationToken = default)
    {
        lock (Store.Gate)
        {
            var resourceTags = TagsOfResource(request.ResourceArn);
            Record("ListTagsOfResource", request.ResourceArn);
            return Task.FromResult(new ListTagsOfResourceResponse
            {
                Tags = resourceTags.Select(t => new Tag { Key = t.Key, Value = t.Value }).ToList(),
            });
        }
    }

    // Limits and endpoints

    public Task<DescribeLimitsResponse> DescribeLimitsAsync(DescribeLimitsRequest request, CancellationToken cancellationToken = default)
    {
        Record("DescribeLimits");
        return Task.FromResult(new DescribeLimitsResponse
        {
            AccountMaxReadCapacityUnits = 80000,
            AccountMaxWriteCapacityUnits = 80000,
            TableMaxReadCapacityUnits = 40000,
            TableMaxWriteCapacityUnits = 40000,
        });
    }

    public Task<DescribeEndpointsResponse> DescribeEndpointsAsync(DescribeEndpointsRequest request, CancellationToken cancellationToken = default)
    {
        Record("DescribeEndpoints");
        return Task.FromResult(new DescribeEndpointsResponse
        {
            Endpoints = [new Model.Endpoint { Address = $"dynamodb.{Region}.amazonaws.com", CachePeriodInMinutes = 1440 }],
        });
    }
}

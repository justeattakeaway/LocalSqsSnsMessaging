using InMemoryAws.DynamoDb.Expressions;
using InMemoryAws.DynamoDb.Model;

namespace InMemoryAws.DynamoDb;

/// <summary>BatchGetItem, BatchWriteItem, TransactGetItems and TransactWriteItems.</summary>
internal sealed partial class InternalDynamoDbClient
{
    private const int MaxBatchGetKeys = 100;
    private const int MaxBatchWriteRequests = 25;
    private const int MaxTransactItems = 100;
    private static readonly TimeSpan TransactionTokenLifetime = TimeSpan.FromMinutes(10);

    /// <summary>A transaction's items, or the items it reads, can't total more than 4 MB.</summary>
    private const long MaxTransactionPayloadBytes = 4 * 1024 * 1024;

    private const string MultipleOperationsOnOneItem = "Transaction request cannot include multiple operations on one item";

    private const string EnvelopePrefix = "1 validation error detected: ";

    /// <summary>A BatchGetItem response carries at most 16 MB of items; the keys past that come back unprocessed.</summary>
    private const long MaxBatchGetResponseBytes = 16 * 1024 * 1024;

    public Task<BatchGetItemResponse> BatchGetItemAsync(BatchGetItemRequest request, CancellationToken cancellationToken = default)
    {
        var requestItems = request.RequestItems!;
        if (requestItems.Values.Sum(k => k.Keys?.Count ?? 0) > MaxBatchGetKeys)
        {
            throw DynamoDbErrors.Validation("Too many items requested for the BatchGetItem call");
        }

        // A request states its projections the legacy way or the expression way, whichever tables they're for.
        LegacyConditions.CheckNoMix(
            [("AttributesToGet", requestItems.Values.Any(k => k.AttributesToGet is not null))],
            [("ProjectionExpression", requestItems.Values.Any(k => k.ProjectionExpression is not null))]);

        // The tables are read in name order, so which keys a response too large to answer at once
        // defers doesn't depend on the order of the request's map.
        var plans = new List<(string RequestName, string Name, KeysAndAttributes Request, IReadOnlyList<DocumentPath>? Projection)>();
        foreach (var (tableName, keys) in requestItems.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            var attributes = ReadAttributes(keys.ExpressionAttributeNames, null, keys.ProjectionExpression);
            var projection = keys.ProjectionExpression is null ? null : ExpressionParser.ParseProjection(keys.ProjectionExpression, attributes);
            CheckAttributesToGet(keys.AttributesToGet);
            var seen = new List<Dictionary<string, DynamoValue>>();
            foreach (var key in keys.Keys ?? [])
            {
                CheckKeyValues(key, enveloped: false);

                // The same key twice is refused before any table is looked up.
                if (seen.Any(other => SameAttributes(other, key)))
                {
                    throw DynamoDbErrors.Validation("Provided list of item keys contains duplicates");
                }

                seen.Add(key);
            }

            plans.Add((tableName, TableName(tableName), keys, projection));
        }

        lock (Store.Gate)
        {
            var responses = new Dictionary<string, List<Dictionary<string, DynamoValue>>>(StringComparer.Ordinal);
            var unprocessed = new Dictionary<string, KeysAndAttributes>(StringComparer.Ordinal);
            var capacity = new List<ConsumedCapacity>();
            var resolved = new List<(DynamoTable Table, string RequestName, KeysAndAttributes Request, IReadOnlyList<DocumentPath>? Projection, List<(DynamoKey Key, Dictionary<string, DynamoValue> Map)> Keys)>();
            foreach (var (requestName, name, keys, projection) in plans)
            {
                var table = ActiveTable(name);
                var tableKeys = new List<(DynamoKey Key, Dictionary<string, DynamoValue> Map)>();
                foreach (var key in keys.Keys ?? [])
                {
                    var tableKey = KeyFromKeyMap(table, key, enveloped: false);
                    if (tableKeys.Any(k => k.Key.Equals(tableKey)))
                    {
                        throw DynamoDbErrors.Validation("Provided list of item keys contains duplicates");
                    }

                    tableKeys.Add((tableKey, key));
                }

                resolved.Add((table, requestName, keys, projection, tableKeys));
            }

            var bytes = 0L;
            var processed = 0;
            var full = false;
            foreach (var (table, requestName, keys, projection, tableKeys) in resolved)
            {
                var found = new List<Dictionary<string, DynamoValue>>();
                var withheld = new List<Dictionary<string, DynamoValue>>();
                var units = 0.0;
                foreach (var (key, map) in tableKeys)
                {
                    var item = full ? null : table.Get(key);
                    var projected = item is null ? null : Project(item, projection, keys.AttributesToGet);
                    var size = projected is null ? 0 : DynamoValueRules.ItemSize(projected);
                    if (full || (processed > 0 && bytes + size > MaxBatchGetResponseBytes))
                    {
                        // Past what one response carries: the key is left for the caller to retry.
                        full = true;
                        withheld.Add(map);
                        continue;
                    }

                    processed++;
                    bytes += size;
                    units += ReadUnits(table.BilledSize(item), keys.ConsistentRead ?? false);
                    if (projected is not null)
                    {
                        found.Add(projected);
                    }
                }

                responses[requestName] = found;
                if (withheld.Count > 0)
                {
                    unprocessed[requestName] = new KeysAndAttributes
                    {
                        Keys = withheld,
                        AttributesToGet = keys.AttributesToGet,
                        ConsistentRead = keys.ConsistentRead,
                        ExpressionAttributeNames = keys.ExpressionAttributeNames,
                        ProjectionExpression = keys.ProjectionExpression,
                    };
                }

                if (Consumed(request.ReturnConsumedCapacity, table, units, write: false) is { } consumed)
                {
                    capacity.Add(consumed);
                }

                Record("BatchGetItem", table.Arn);
            }

            return Task.FromResult(new BatchGetItemResponse
            {
                Responses = responses,
                UnprocessedKeys = unprocessed,
                ConsumedCapacity = capacity.Count > 0 ? capacity : null,
            });
        }
    }

    public Task<BatchWriteItemResponse> BatchWriteItemAsync(BatchWriteItemRequest request, CancellationToken cancellationToken = default)
    {
        var requestItems = request.RequestItems!;
        if (requestItems.Values.Sum(r => r.Count) > MaxBatchWriteRequests)
        {
            throw DynamoDbErrors.Validation("Too many items requested for the BatchWriteItem call");
        }

        var writes = new List<(string Name, Dictionary<string, DynamoValue>? Item, Dictionary<string, DynamoValue>? Written, Dictionary<string, DynamoValue>? Key)>();
        foreach (var (tableName, requests) in requestItems)
        {
            foreach (var write in requests)
            {
                if ((write.PutRequest is null) == (write.DeleteRequest is null))
                {
                    throw DynamoDbErrors.Validation("Supplied AttributeValue has more than one datatypes set, must contain exactly one of the supported datatypes");
                }

                // What BatchWriteItem refuses about one request's item it states bare.
                var item = write.PutRequest is { Item: { } putItem }
                    ? InElement(() =>
                    {
                        CheckMapKeysNotEmpty(putItem);
                        return CanonicalItem(putItem);
                    })
                    : null;
                writes.Add((TableName(tableName), item, write.PutRequest?.Item, write.DeleteRequest?.Key));
            }
        }

        lock (Store.Gate)
        {
            var planned = new List<(DynamoTable Table, DynamoKey Key, Dictionary<string, DynamoValue>? Item)>();
            foreach (var (name, canonical, written, key) in writes)
            {
                var table = ActiveTable(name);
                var item = canonical is null ? null : WithVectorsAsWritten(table, canonical, written!);
                DynamoKey tableKey;
                if (item is not null)
                {
                    CheckIndexKeys(table, item, update: false);
                    tableKey = KeyFromItem(table, item, batch: true);
                    CheckLocalIndexEntrySizes(table, item);
                }
                else
                {
                    tableKey = KeyFromKeyMap(table, key!);
                }

                if (planned.Any(p => ReferenceEquals(p.Table, table) && p.Key.Equals(tableKey)))
                {
                    throw DynamoDbErrors.Validation("Provided list of item keys contains duplicates");
                }

                planned.Add((table, tableKey, item));
            }

            var units = new Dictionary<DynamoTable, (double Table, Dictionary<string, double> Indexes)>();
            var vectorBytes = new Dictionary<DynamoTable, Dictionary<string, double>>();
            var metrics = new Dictionary<string, List<ItemCollectionMetrics>>(StringComparer.Ordinal);
            foreach (var (table, key, item) in planned)
            {
                var old = table.Get(key);
                var change = item is not null ? table.Put(key, item) : table.Delete(key);
                if (item is not null || old is not null)
                {
                    Changed(table, change);
                }

                var (tableUnits, indexUnits) = units.TryGetValue(table, out var sum) ? sum : (0.0, new Dictionary<string, double>(StringComparer.Ordinal));
                tableUnits += WriteUnits(table, old, item);
                foreach (var (index, cost) in IndexWriteUnits(table, old, item))
                {
                    indexUnits[index] = indexUnits.TryGetValue(index, out var running) ? running + cost : cost;
                }

                AddVectorWriteBytes(vectorBytes, table, old, item);

                units[table] = (tableUnits, indexUnits);
                if (CollectionMetrics(request.ReturnItemCollectionMetrics, table, key) is { } metric)
                {
                    (metrics.TryGetValue(table.Name, out var list) ? list : metrics[table.Name] = []).Add(metric);
                }

                Record("BatchWriteItem", table.Arn);
            }

            var capacity = units.Select(u => Consumed(request.ReturnConsumedCapacity, u.Key, u.Value.Table, write: true, u.Value.Indexes, vectorBytes: vectorBytes.TryGetValue(u.Key, out var vectors) ? vectors : null)).Where(c => c is not null).Select(c => c!).ToList();
            return Task.FromResult(new BatchWriteItemResponse
            {
                UnprocessedItems = new Dictionary<string, List<WriteRequest>>(StringComparer.Ordinal),
                ItemCollectionMetrics = request.ReturnItemCollectionMetrics == "SIZE" ? metrics : null,
                ConsumedCapacity = capacity.Count > 0 ? capacity : null,
            });
        }
    }

    /// <summary>BatchWriteItem refuses a map, at any depth of an item, that holds an attribute with an empty name.</summary>
    private static void CheckMapKeysNotEmpty(IReadOnlyDictionary<string, DynamoValue> map)
    {
        foreach (var value in map.Values)
        {
            Check(value);
        }

        static void Check(DynamoValue value)
        {
            if (value.Type == DynamoType.M)
            {
                if (value.M.Keys.Any(k => k.Length == 0))
                {
                    throw DynamoDbErrors.Validation("Empty attribute name");
                }

                foreach (var nested in value.M.Values)
                {
                    Check(nested);
                }
            }
            else if (value.Type == DynamoType.L)
            {
                foreach (var nested in value.L)
                {
                    Check(nested);
                }
            }
        }
    }

    public Task<TransactGetItemsResponse> TransactGetItemsAsync(TransactGetItemsRequest request, CancellationToken cancellationToken = default)
    {
        var items = request.TransactItems!;
        if (items.Count > MaxTransactItems)
        {
            throw DynamoDbErrors.ValidationClause($"Value at 'TransactItems' failed to satisfy constraint: Member must have length less than or equal to {MaxTransactItems}");
        }

        // Every element's own members are checked before any table is looked up.
        var plans = new List<(Get Get, string Name, IReadOnlyList<DocumentPath>? Projection)>();
        for (var i = 0; i < items.Count; i++)
        {
            var get = items[i].Get!;
            var name = TransactTableName(get.TableName!, i, "get");
            CheckKeyValues(get.Key!, enveloped: false);
            IReadOnlyList<DocumentPath>? projection = null;
            if (get.ProjectionExpression is { } expression)
            {
                var attributes = ReadAttributes(get.ExpressionAttributeNames, null, expression);
                projection = ExpressionParser.ParseProjection(expression, attributes);
            }

            plans.Add((get, name, projection));
        }

        lock (Store.Gate)
        {
            var resolved = new List<(DynamoTable Table, DynamoKey Key, IReadOnlyList<DocumentPath>? Projection)>();
            for (var i = 0; i < plans.Count; i++)
            {
                var (get, name, projection) = plans[i];
                var table = ActiveTable(name);
                CheckKeyValuesNotEmpty(table, get.Key!);
                CheckKeyNumbers(get.Key!);
                var key = CancelledBy(i, plans.Count, () => KeyFromKeyMap(table, get.Key!, enveloped: false));
                if (resolved.Any(r => ReferenceEquals(r.Table, table) && r.Key.Equals(key)))
                {
                    throw DynamoDbErrors.Validation(MultipleOperationsOnOneItem);
                }

                resolved.Add((table, key, projection));
            }

            var responses = new List<ItemResponse>();
            var units = new Dictionary<DynamoTable, double>();
            var payload = 0L;
            foreach (var (table, key, projection) in resolved)
            {
                var item = table.Get(key);
                var projected = item is null ? null : NonEmpty(Project(item, projection, null));
                payload += projected is null ? 0 : DynamoValueRules.ItemSize(projected);
                responses.Add(new ItemResponse { Item = projected });
                units[table] = (units.TryGetValue(table, out var running) ? running : 0) + ReadUnits(table.BilledSize(item), consistent: true, transactional: true);
            }

            if (payload > MaxTransactionPayloadBytes)
            {
                throw DynamoDbErrors.Validation("Payload in response is too large.");
            }

            foreach (var (table, _, _) in resolved)
            {
                Record("TransactGetItems", table.Arn);
            }

            var capacity = units.Select(u => Consumed(request.ReturnConsumedCapacity, u.Key, u.Value, write: false, transactional: true)).Where(c => c is not null).Select(c => c!).ToList();
            return Task.FromResult(new TransactGetItemsResponse
            {
                Responses = responses,
                ConsumedCapacity = capacity.Count > 0 ? capacity : null,
            });
        }
    }

    /// <summary>One item of a TransactWriteItems request, its own members checked, before its table is looked up.</summary>
    private sealed record TransactAction(
        string Name,
        Dictionary<string, DynamoValue>? Key,
        Dictionary<string, DynamoValue>? Item,
        ExpressionCondition? Condition,
        string? ReturnValuesOnConditionCheckFailure,
        UpdateProgram? Update,
        bool Delete);

    /// <summary>One item of a TransactWriteItems request, validated against its table and ready to apply.</summary>
    private sealed class TransactWrite
    {
        public required DynamoTable Table { get; init; }

        public required DynamoKey Key { get; init; }

        public ExpressionCondition? Condition { get; init; }

        public string? ReturnValuesOnConditionCheckFailure { get; init; }

        /// <summary>The item before the transaction.</summary>
        public Dictionary<string, DynamoValue>? Old { get; init; }

        /// <summary>The item a Put or Update leaves.</summary>
        public Dictionary<string, DynamoValue>? After { get; init; }

        public bool Delete { get; init; }

        /// <summary>An Update of an absent item that only takes things away, which writes nothing.</summary>
        public bool CreatesNothing { get; init; }

        /// <summary>The refusal an Update met applying itself to the stored item, reported once its condition holds.</summary>
        public string? UpdateFailure { get; init; }

        public bool Check => After is null && !Delete && !CreatesNothing;
    }

    public Task<TransactWriteItemsResponse> TransactWriteItemsAsync(TransactWriteItemsRequest request, CancellationToken cancellationToken = default)
    {
        var items = request.TransactItems!;
        if (items.Count > MaxTransactItems)
        {
            throw DynamoDbErrors.ValidationClause($"Value at 'TransactItems' failed to satisfy constraint: Member must have length less than or equal to {MaxTransactItems}");
        }

        // Every element's own members are checked, in request order, before any table is looked up,
        // so which defect a malformed transaction is refused for doesn't depend on the tables.
        var actions = new List<TransactAction>();
        for (var i = 0; i < items.Count; i++)
        {
            actions.Add(ParseTransactAction(items[i], i));
        }

        var payload = actions.Sum(a => a.Item is null ? 0 : DynamoValueRules.ItemSize(a.Item));
        if (payload > MaxTransactionPayloadBytes)
        {
            throw DynamoDbErrors.Validation($"Transaction payload size cannot exceed 4MB. Payload Size: {payload.ToString(CultureInfo.InvariantCulture)}");
        }

        lock (Store.Gate)
        {
            var now = _cloud.TimeProvider.GetUtcNow();
            string? fingerprint = null;
            if (request.ClientRequestToken is { } token)
            {
                foreach (var expired in Store.TransactionTokens.Where(t => t.Value.ExpiresAt <= now).Select(t => t.Key).ToList())
                {
                    Store.TransactionTokens.Remove(expired);
                }

                // The options that choose what the response reports are part of the request a token stands for.
                fingerprint = string.Join("|", items.Select(i => i.ToString())) + "|" + request.ReturnConsumedCapacity + "|" + request.ReturnItemCollectionMetrics;
                if (Store.TransactionTokens.TryGetValue(token, out var remembered))
                {
                    if (!string.Equals(remembered.RequestFingerprint, fingerprint, StringComparison.Ordinal))
                    {
                        throw new DynamoDbException("IdempotentParameterMismatchException", "Specified idempotent token was used with different request parameters within the idempotency window");
                    }

                    // The same request again: it already happened, so it writes nothing, and is billed
                    // as the transactional read of the items it addresses.
                    return Task.FromResult(new TransactWriteItemsResponse { ConsumedCapacity = ReplayCapacity(request.ReturnConsumedCapacity, actions) });
                }
            }

            var writes = new List<TransactWrite>();
            for (var i = 0; i < actions.Count; i++)
            {
                writes.Add(PrepareTransactWrite(actions[i], i, actions.Count, writes));
            }

            // Every condition is read against the items as they stood before the transaction; one
            // that fails cancels the whole transaction.
            var reasons = new List<CancellationReason>();
            foreach (var write in writes)
            {
                var holds = write.Condition is null || InElement(() => write.Condition.Evaluate(write.Old));
                reasons.Add(!holds
                    ? new CancellationReason("ConditionalCheckFailed", DynamoDbConditionalCheckFailedException.DefaultMessage, write.ReturnValuesOnConditionCheckFailure == "ALL_OLD" ? write.Old : null)
                    : write.UpdateFailure is { } failure
                        ? new CancellationReason("ValidationError", failure)
                        : CancellationReason.None);
            }

            if (reasons.Any(r => r.Code != "None"))
            {
                throw new DynamoDbTransactionCanceledException(reasons);
            }

            var units = new Dictionary<DynamoTable, (double Table, Dictionary<string, double> Indexes)>();
            var vectorBytes = new Dictionary<DynamoTable, Dictionary<string, double>>();
            var metrics = new Dictionary<string, List<ItemCollectionMetrics>>(StringComparer.Ordinal);
            foreach (var write in writes)
            {
                if (write.Delete)
                {
                    if (write.Old is not null)
                    {
                        Changed(write.Table, write.Table.Delete(write.Key));
                    }
                }
                else if (write.After is { } item)
                {
                    Changed(write.Table, write.Table.Put(write.Key, item));
                }

                var (tableUnits, indexUnits) = units.TryGetValue(write.Table, out var sum) ? sum : (0.0, new Dictionary<string, double>(StringComparer.Ordinal));
                tableUnits += write.Check
                    ? ReadUnits(write.Table.BilledSize(write.Old), consistent: true, transactional: true)
                    : WriteUnits(write.Table, write.Old, write.After, transactional: true);
                if (!write.Check)
                {
                    // Index entries are written once the transaction commits, at the standard rate.
                    foreach (var (index, cost) in IndexWriteUnits(write.Table, write.Old, write.Delete ? null : write.After))
                    {
                        indexUnits[index] = (indexUnits.TryGetValue(index, out var indexSum) ? indexSum : 0) + cost;
                    }

                    AddVectorWriteBytes(vectorBytes, write.Table, write.Old, write.Delete ? null : write.After);
                    if (CollectionMetrics(request.ReturnItemCollectionMetrics, write.Table, write.Key) is { } metric)
                    {
                        (metrics.TryGetValue(write.Table.Name, out var list) ? list : metrics[write.Table.Name] = []).Add(metric);
                    }
                }

                units[write.Table] = (tableUnits, indexUnits);
                Record("TransactWriteItems", write.Table.Arn);
            }

            if (request.ClientRequestToken is { } requestToken)
            {
                Store.TransactionTokens[requestToken] = new DynamoTransactionToken(fingerprint!, now + TransactionTokenLifetime);
            }

            var capacity = units.Select(u => Consumed(request.ReturnConsumedCapacity, u.Key, u.Value.Table, write: true, u.Value.Indexes, transactional: true, vectorBytes.TryGetValue(u.Key, out var vectors) ? vectors : null)).Where(c => c is not null).Select(c => c!).ToList();
            return Task.FromResult(new TransactWriteItemsResponse
            {
                ConsumedCapacity = capacity.Count > 0 ? capacity : null,
                ItemCollectionMetrics = metrics.Count > 0 ? metrics : null,
            });
        }
    }

    /// <summary>
    /// The checks of one TransactWriteItems element that need no table: its table name, its
    /// expressions and their placeholders, and the item a Put carries.
    /// </summary>
    private TransactAction ParseTransactAction(TransactWriteItem item, int at)
    {
        var stated = (item.ConditionCheck is null ? 0 : 1) + (item.Put is null ? 0 : 1) + (item.Delete is null ? 0 : 1) + (item.Update is null ? 0 : 1);
        if (stated != 1)
        {
            throw DynamoDbErrors.Validation("TransactItems can only contain one of Check, Put, Update or Delete");
        }

        (string Member, string? TableName, Dictionary<string, string>? Names, Dictionary<string, DynamoValue>? Values, string? ConditionExpression, string? UpdateExpression, string? ReturnOnFailure) element = item switch
        {
            { ConditionCheck: { } c } => ("conditionCheck", c.TableName, c.ExpressionAttributeNames, c.ExpressionAttributeValues, c.ConditionExpression, null, c.ReturnValuesOnConditionCheckFailure),
            { Put: { } p } => ("put", p.TableName, p.ExpressionAttributeNames, p.ExpressionAttributeValues, p.ConditionExpression, null, p.ReturnValuesOnConditionCheckFailure),
            { Delete: { } d } => ("delete", d.TableName, d.ExpressionAttributeNames, d.ExpressionAttributeValues, d.ConditionExpression, null, d.ReturnValuesOnConditionCheckFailure),
            _ => ("update", item.Update!.TableName, item.Update.ExpressionAttributeNames, item.Update.ExpressionAttributeValues, item.Update.ConditionExpression, item.Update.UpdateExpression, item.Update.ReturnValuesOnConditionCheckFailure),
        };
        var (member, tableName, names, values, conditionExpression, updateExpression, returnOnFailure) = element;

        var name = TransactTableName(tableName!, at, member);
        return InElement(() =>
        {
            CheckReturnValuesOnConditionCheckFailure(returnOnFailure);

            // A transaction ignores placeholders when its element states no expression to use them in.
            var expressive = conditionExpression is not null || updateExpression is not null;
            var attributes = Attributes(
                expressive ? names : null,
                expressive ? values : null,
                ("UpdateExpression", updateExpression),
                ("ConditionExpression", conditionExpression));
            var update = updateExpression is null ? null : ExpressionParser.ParseUpdate(updateExpression, attributes);
            var condition = conditionExpression is null ? null : ExpressionParser.ParseCondition("ConditionExpression", conditionExpression, attributes);
            return item switch
            {
                { ConditionCheck: { } c } => new TransactAction(name, c.Key, null, condition, returnOnFailure, null, false),
                { Put: { } p } => new TransactAction(name, null, CanonicalItem(p.Item!), condition, returnOnFailure, null, false),
                { Delete: { } d } => new TransactAction(name, d.Key, null, condition, returnOnFailure, null, true),
                _ => new TransactAction(name, item.Update!.Key, null, condition, returnOnFailure, update, false),
            };
        });
    }

    /// <summary>
    /// One action resolved against its table. What the request itself gets wrong (an empty key, an
    /// update past the item size limit) refuses the call; what only the table's schema makes wrong
    /// (a key of the wrong type or size, an index key of the wrong type, an update of a key
    /// attribute) cancels the transaction for this element.
    /// </summary>
    private TransactWrite PrepareTransactWrite(TransactAction action, int at, int count, List<TransactWrite> earlier)
    {
        var table = ActiveTable(action.Name);
        DynamoKey key;
        if (action.Item is { } put)
        {
            CancelledBy(at, count, () => CheckIndexKeyTypes(table, put));
            CheckIndexKeyValues(table, put, update: false);
            CheckKeyValuesNotEmpty(table, put);
            key = CancelledBy(at, count, () => KeyFromItem(table, put));
            CheckLocalIndexEntrySizes(table, put);
        }
        else
        {
            CheckKeyValuesNotEmpty(table, action.Key!);
            CheckKeyNumbers(action.Key!);
            key = CancelledBy(at, count, () => KeyFromKeyMap(table, action.Key!, enveloped: false));
        }

        var old = table.Get(key);
        var after = action.Item;
        var createsNothing = false;
        string? updateFailure = null;
        if (action.Update is { } update)
        {
            foreach (var change in update.Actions)
            {
                if (table.KeySchema.IsKeyAttribute(change.Path.AttributeName))
                {
                    CancelledBy(at, count, () => throw DynamoDbErrors.InvalidParameter($"Cannot update attribute {change.Path.AttributeName}. This attribute is part of the key"));
                }
            }

            // As UpdateItem: the update's own request shape is held to the item limit first, and
            // refuses the whole request. An item that only grows past the limit once the update is
            // applied cancels the transaction instead.
            if (update.RequestShapeBytes() > DynamoValueRules.MaxItemBytes)
            {
                throw DynamoDbErrors.Validation(DynamoValueRules.UpdateItemSizeExceeded);
            }

            var updated = old is null ? table.KeySchema.ToAttributes(key) : new Dictionary<string, DynamoValue>(old, StringComparer.Ordinal);
            after = updated;
            try
            {
                InElement(() => update.Apply(updated, itemExists: old is not null));
            }
            catch (DynamoDbException refusal) when (refusal.ErrorCode == "ValidationException" && IsStoredItemRefusal(refusal.Message))
            {
                // What only the stored item makes wrong (an operand of the wrong type, a path
                // through a scalar) cancels the transaction with this element's own reason, once
                // its condition is known to hold.
                updateFailure = refusal.Message;
            }
        }

        if (action.Update is { } applied && updateFailure is null)
        {
            var updated = after!;
            if (DynamoValueRules.ExceedsDepth(updated))
            {
                throw DynamoDbErrors.Validation(DynamoValueRules.NestingExceeded);
            }

            CancelledBy(at, count, () => DynamoValueRules.CheckItemLimits(updated, DynamoValueRules.UpdateItemSizeExceeded));
            InElement(() => CheckLocalIndexEntrySizes(table, updated, DynamoValueRules.UpdateItemSizeExceeded));
            CancelledBy(at, count, () => CheckIndexKeyTypes(table, updated));
            CheckIndexKeyValues(table, updated, update: true);

            // As UpdateItem: an update whose every action only takes something away adds no item where there was none.
            createsNothing = old is null && applied.Actions.Count > 0 && updated.Keys.All(table.KeySchema.IsKeyAttribute);
            after = createsNothing ? null : updated;
        }
        else if (updateFailure is not null)
        {
            after = null;
        }

        if (earlier.Any(w => ReferenceEquals(w.Table, table) && w.Key.Equals(key)))
        {
            throw DynamoDbErrors.Validation(MultipleOperationsOnOneItem);
        }

        return new TransactWrite
        {
            Table = table,
            Key = key,
            Condition = action.Condition,
            ReturnValuesOnConditionCheckFailure = action.ReturnValuesOnConditionCheckFailure,
            Old = old,
            After = after,
            Delete = action.Delete,
            CreatesNothing = createsNothing,
            UpdateFailure = updateFailure,
        };
    }

    /// <summary>Whether an update's refusal comes from the stored item it met rather than from the request.</summary>
    private static bool IsStoredItemRefusal(string message) =>
        message is UpdateProgram.IncorrectOperandType or DocumentPath.InvalidForUpdate or UpdateProgram.MissingOperandAttribute;

    /// <summary>A replayed transaction's capacity: the transactional read of each item it addresses.</summary>
    private List<ConsumedCapacity>? ReplayCapacity(string? returnConsumedCapacity, List<TransactAction> actions)
    {
        var units = new Dictionary<DynamoTable, double>();
        foreach (var action in actions)
        {
            var table = ActiveTable(action.Name);
            var item = table.Get(action.Item is { } put ? KeyFromItem(table, put) : KeyFromKeyMap(table, action.Key!, enveloped: false));
            units[table] = (units.TryGetValue(table, out var running) ? running : 0) + ReadUnits(item is null ? 0 : DynamoValueRules.ItemSize(item), consistent: true, transactional: true);
        }

        var capacity = units.Select(u => Consumed(returnConsumedCapacity, u.Key, u.Value, write: false, transactional: true)).Where(c => c is not null).Select(c => c!).ToList();
        return capacity.Count > 0 ? capacity : null;
    }

    /// <summary>
    /// The table a transaction element names, checked as the element's member: a table ARN in
    /// another region is refused rather than looked up.
    /// </summary>
    private string TransactTableName(string tableName, int at, string member)
    {
        var path = $"transactItems.{(at + 1).ToString(CultureInfo.InvariantCulture)}.member.{member}.tableName";
        if (Arn.TryParse(tableName, out var arn)
            && string.Equals(arn.Service, "dynamodb", StringComparison.Ordinal)
            && !string.Equals(arn.Region, Region, StringComparison.Ordinal))
        {
            throw DynamoDbErrors.ValidationClause(AwsValidation.Clause(tableName, path, "Invalid AWS region"));
        }

        var name = TableName(tableName);
        CheckTableName(name, path);
        return name;
    }

    /// <summary>Whether two key maps name the same attributes with equal values.</summary>
    private static bool SameAttributes(Dictionary<string, DynamoValue> a, Dictionary<string, DynamoValue> b) =>
        a.Count == b.Count && a.All(entry => b.TryGetValue(entry.Key, out var other) && entry.Value.Equals(other));

    /// <summary>Refuses a key whose key attribute holds an empty string or binary, before the key is matched to the schema.</summary>
    private static void CheckKeyValuesNotEmpty(DynamoTable table, Dictionary<string, DynamoValue> map)
    {
        foreach (var attribute in table.KeySchema.Attributes)
        {
            if (map.TryGetValue(attribute.Name, out var value))
            {
                CheckKeyValueNotEmpty(attribute.Name, value);
            }
        }
    }

    /// <summary>
    /// Runs a check of one element of a transaction or batch: DynamoDB states what it refuses about
    /// an element's own members bare, outside the validation envelope.
    /// </summary>
    private static T InElement<T>(Func<T> check)
    {
        try
        {
            return check();
        }
        catch (DynamoDbException refusal) when (refusal.ErrorCode == "ValidationException" && refusal.Message.StartsWith(EnvelopePrefix, StringComparison.Ordinal))
        {
            throw DynamoDbErrors.Validation(refusal.Message[EnvelopePrefix.Length..]);
        }
    }

    private static void InElement(Action check) => InElement(() =>
    {
        check();
        return true;
    });

    /// <summary>
    /// Runs a check the element at <paramref name="at"/> is held to against its table's schema: a
    /// refusal cancels the transaction, with a <c>ValidationError</c> reason for that element.
    /// </summary>
    private static T CancelledBy<T>(int at, int count, Func<T> check)
    {
        try
        {
            return check();
        }
        catch (DynamoDbException refusal) when (refusal.ErrorCode == "ValidationException")
        {
            var message = refusal.Message.StartsWith(EnvelopePrefix, StringComparison.Ordinal) ? refusal.Message[EnvelopePrefix.Length..] : refusal.Message;
            throw new DynamoDbTransactionCanceledException(Enumerable.Range(0, count)
                .Select(i => i == at ? new CancellationReason("ValidationError", message) : CancellationReason.None)
                .ToList());
        }
    }

    private static void CancelledBy(int at, int count, Action check) => CancelledBy(at, count, () =>
    {
        check();
        return true;
    });
}

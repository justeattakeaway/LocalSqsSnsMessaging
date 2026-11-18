using InMemoryAws.DynamoDb.Expressions;
using InMemoryAws.DynamoDb.Model;

namespace InMemoryAws.DynamoDb;

/// <summary>Single-item operations: PutItem, GetItem, UpdateItem and DeleteItem.</summary>
internal sealed partial class InternalDynamoDbClient
{
    private const int MaxPartitionKeyBytes = 2048;
    private const int MaxSortKeyBytes = 1024;
    private const long MaxExpressionAttributeValuesBytes = 2 * 1024 * 1024;

    public Task<PutItemResponse> PutItemAsync(PutItemRequest request, CancellationToken cancellationToken = default)
    {
        var name = TableName(request.TableName!);
        CheckReturnValuesOnConditionCheckFailure(request.ReturnValuesOnConditionCheckFailure);
        LegacyConditions.CheckNoMix(
            [("Expected", request.Expected is { Count: > 0 }), ("ConditionalOperator", request.ConditionalOperator is not null)],
            [("ConditionExpression", request.ConditionExpression is not null)]);
        var attributes = Attributes(request.ExpressionAttributeNames, request.ExpressionAttributeValues, ("ConditionExpression", request.ConditionExpression));
        if (request.ReturnValues is not (null or "NONE" or "ALL_OLD"))
        {
            throw DynamoDbErrors.ValidationClause("ReturnValues can only be ALL_OLD or NONE");
        }

        // PutItem sizes the item only once it has found the table (BatchWriteItem sizes it first).
        var item = CanonicalItem(request.Item!, sized: false);
        var condition = WriteCondition(request.ConditionExpression, request.Expected, request.ConditionalOperator, attributes);

        lock (Store.Gate)
        {
            var table = ActiveTable(name);
            Authorize(table, "PutItem");
            CheckItemSize(item);
            item = WithVectorsAsWritten(table, item, request.Item!);
            CheckIndexKeys(table, item, update: false);
            var key = KeyFromItem(table, item);
            CheckLocalIndexEntrySizes(table, item);
            var old = table.Get(key);
            CheckCondition(condition, old, request.ReturnValuesOnConditionCheckFailure);
            var change = table.Put(key, item);
            Changed(table, change);
            Record("PutItem", table.Arn);
            return Task.FromResult(new PutItemResponse
            {
                Attributes = request.ReturnValues == "ALL_OLD" ? old : null,
                ConsumedCapacity = Consumed(request.ReturnConsumedCapacity, table, WriteUnits(table, old, item), write: true, IndexWriteUnits(table, old, item), vectorBytes: VectorWriteBytes(table, old, item)),
                ItemCollectionMetrics = CollectionMetrics(request.ReturnItemCollectionMetrics, table, key),
            });
        }
    }

    public Task<GetItemResponse> GetItemAsync(GetItemRequest request, CancellationToken cancellationToken = default)
    {
        var name = TableName(request.TableName!);
        LegacyConditions.CheckNoMix([("AttributesToGet", request.AttributesToGet is not null)], [("ProjectionExpression", request.ProjectionExpression is not null)]);
        var attributes = ReadAttributes(request.ExpressionAttributeNames, null, request.ProjectionExpression);
        var projection = request.ProjectionExpression is null ? null : ExpressionParser.ParseProjection(request.ProjectionExpression, attributes);
        CheckAttributesToGet(request.AttributesToGet);

        CheckKeyValues(request.Key!, enveloped: false);

        lock (Store.Gate)
        {
            var table = ActiveTable(name);
            Authorize(table, "GetItem");
            var key = KeyFromKeyMap(table, request.Key!, enveloped: false);
            var item = table.Get(key);
            var consistent = request.ConsistentRead ?? false;
            Record("GetItem", table.Arn);
            return Task.FromResult(new GetItemResponse
            {
                Item = item is null ? null : Project(item, projection, request.AttributesToGet),
                ConsumedCapacity = Consumed(request.ReturnConsumedCapacity, table, ReadUnits(table.BilledSize(item), consistent), write: false),
            });
        }
    }

    public Task<DeleteItemResponse> DeleteItemAsync(DeleteItemRequest request, CancellationToken cancellationToken = default)
    {
        var name = TableName(request.TableName!);
        CheckReturnValuesOnConditionCheckFailure(request.ReturnValuesOnConditionCheckFailure);
        LegacyConditions.CheckNoMix(
            [("Expected", request.Expected is { Count: > 0 }), ("ConditionalOperator", request.ConditionalOperator is not null)],
            [("ConditionExpression", request.ConditionExpression is not null)]);
        var attributes = Attributes(request.ExpressionAttributeNames, request.ExpressionAttributeValues, ("ConditionExpression", request.ConditionExpression));
        if (request.ReturnValues is not (null or "NONE" or "ALL_OLD"))
        {
            throw DynamoDbErrors.ValidationClause("ReturnValues can only be ALL_OLD or NONE");
        }

        var condition = WriteCondition(request.ConditionExpression, request.Expected, request.ConditionalOperator, attributes);
        CheckKeyValues(request.Key!, enveloped: true);

        lock (Store.Gate)
        {
            var table = ActiveTable(name);
            Authorize(table, "DeleteItem");
            var key = KeyFromKeyMap(table, request.Key!);
            var old = table.Get(key);
            CheckCondition(condition, old, request.ReturnValuesOnConditionCheckFailure);
            if (old is not null)
            {
                Changed(table, table.Delete(key));
            }

            Record("DeleteItem", table.Arn);
            return Task.FromResult(new DeleteItemResponse
            {
                Attributes = request.ReturnValues == "ALL_OLD" ? old : null,
                ConsumedCapacity = Consumed(request.ReturnConsumedCapacity, table, WriteUnits(table, old, null), write: true, IndexWriteUnits(table, old, null), vectorBytes: VectorWriteBytes(table, old, null)),
                ItemCollectionMetrics = CollectionMetrics(request.ReturnItemCollectionMetrics, table, key),
            });
        }
    }

    public Task<UpdateItemResponse> UpdateItemAsync(UpdateItemRequest request, CancellationToken cancellationToken = default)
    {
        var name = TableName(request.TableName!);
        CheckReturnValuesOnConditionCheckFailure(request.ReturnValuesOnConditionCheckFailure);
        LegacyConditions.CheckNoMix(
            [
                ("AttributeUpdates", request.AttributeUpdates is { Count: > 0 }),
                ("Expected", request.Expected is { Count: > 0 }),
                ("ConditionalOperator", request.ConditionalOperator is not null),
            ],
            [("UpdateExpression", request.UpdateExpression is not null), ("ConditionExpression", request.ConditionExpression is not null)]);
        var attributes = Attributes(
            request.ExpressionAttributeNames,
            request.ExpressionAttributeValues,
            ("UpdateExpression", request.UpdateExpression),
            ("ConditionExpression", request.ConditionExpression));

        UpdateProgram? update = null;
        if (request.UpdateExpression is not null)
        {
            update = ExpressionParser.ParseUpdate(request.UpdateExpression, attributes);
        }
        else if (request.AttributeUpdates is { Count: > 0 } attributeUpdates)
        {
            foreach (var value in attributeUpdates.Values)
            {
                if (value.Value is not null)
                {
                    value.Value = DynamoValueRules.Canonicalize(value.Value);
                }
            }

            update = UpdateProgram.FromAttributeUpdates(attributeUpdates);
        }

        var condition = WriteCondition(request.ConditionExpression, request.Expected, request.ConditionalOperator, attributes);

        lock (Store.Gate)
        {
            var table = ActiveTable(name);
            Authorize(table, "UpdateItem");
            var key = KeyFromKeyMap(table, request.Key!);
            var old = table.Get(key);

            // The update's request shape is held to the item limit before the condition is read.
            if (request.UpdateExpression is not null && update!.RequestShapeBytes() > DynamoValueRules.MaxItemBytes)
            {
                throw DynamoDbErrors.Validation(DynamoValueRules.UpdateItemSizeExceeded);
            }

            CheckCondition(condition, old, request.ReturnValuesOnConditionCheckFailure);

            var updated = old is null
                ? table.KeySchema.ToAttributes(key)
                : new Dictionary<string, DynamoValue>(old, StringComparer.Ordinal);
            var changed = new List<DocumentPath>();
            if (update is not null)
            {
                foreach (var action in update.Actions)
                {
                    if (table.KeySchema.IsKeyAttribute(action.Path.AttributeName))
                    {
                        throw DynamoDbErrors.InvalidParameter($"Cannot update attribute {action.Path.AttributeName}. This attribute is part of the key");
                    }
                }

                changed = update.Apply(updated);
            }

            CheckIndexKeys(table, updated, update: true);
            DynamoValueRules.CheckItemLimits(updated, DynamoValueRules.UpdateItemSizeExceeded);
            CheckLocalIndexEntrySizes(table, updated, DynamoValueRules.UpdateItemSizeExceeded);

            // An update whose every action only takes something away adds no item where there was none.
            var createsNothing = old is null && update is { Actions.Count: > 0 } && updated.Keys.All(table.KeySchema.IsKeyAttribute);
            if (!createsNothing)
            {
                var change = table.Put(key, updated);
                Changed(table, change);
            }

            Record("UpdateItem", table.Arn);

            Dictionary<string, DynamoValue>? returned = request.ReturnValues switch
            {
                _ when createsNothing => null,
                "ALL_OLD" => old,
                "ALL_NEW" => updated,
                "UPDATED_OLD" => old is null ? null : NonEmpty(ItemProjection.Apply(old, changed)),
                "UPDATED_NEW" => NonEmpty(ItemProjection.Apply(updated, changed.Where(p => p.Get(updated) is not null).ToList())),
                _ => null,
            };

            return Task.FromResult(new UpdateItemResponse
            {
                Attributes = returned,
                ConsumedCapacity = Consumed(request.ReturnConsumedCapacity, table, WriteUnits(table, old, createsNothing ? null : updated), write: true, IndexWriteUnits(table, old, createsNothing ? null : updated), vectorBytes: VectorWriteBytes(table, old, createsNothing ? null : updated)),
                ItemCollectionMetrics = createsNothing ? null : CollectionMetrics(request.ReturnItemCollectionMetrics, table, key),
            });
        }
    }

    private static Dictionary<string, DynamoValue>? NonEmpty(Dictionary<string, DynamoValue> item) => item.Count == 0 ? null : item;

    // Shared by the item, batch and transaction operations.

    private static void CheckReturnValuesOnConditionCheckFailure(string? value)
    {
        if (value is not (null or "NONE" or "ALL_OLD"))
        {
            throw DynamoDbErrors.Validation("ReturnValuesOnConditionCheckFailure can only be ALL_OLD or NONE");
        }
    }

    private static void CheckAttributesToGet(List<string>? attributesToGet)
    {
        if (attributesToGet is null)
        {
            return;
        }

        var duplicate = attributesToGet.GroupBy(a => a, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw DynamoDbErrors.InvalidParameter($"Duplicate value in attribute name: {duplicate.Key}");
        }
    }

    /// <summary>
    /// The request's expression attributes, checked against its expressions before any is read,
    /// with every value canonicalized.
    /// </summary>
    private static ExpressionAttributes Attributes(
        Dictionary<string, string>? names,
        Dictionary<string, DynamoValue>? values,
        params (string Member, string? Expression)[] expressions)
    {
        var attributes = new ExpressionAttributes(names, values);
        try
        {
            attributes.CheckAllReferenced(expressions);
        }
        catch (DynamoDbException unused) when (values is not null && unused.Message.Contains("unused in expressions", StringComparison.Ordinal))
        {
            // A value nested too deeply is refused ahead of a placeholder the request doesn't use.
            if (values.Values.Any(v => v.Depth() > DynamoValueRules.MaxDepth))
            {
                throw DynamoDbErrors.ValidationClause(DynamoValueRules.NestingExceeded);
            }

            throw;
        }

        if (values is not null)
        {
            if (values.Values.Sum(v => v.Size()) > MaxExpressionAttributeValuesBytes)
            {
                throw DynamoDbErrors.ValidationClause("ExpressionAttributeValues exceeds max size");
            }

            // An update's values are held to the item limit by its request shape instead.
            var updating = expressions.Any(e => e.Member == "UpdateExpression" && e.Expression is not null);
            foreach (var placeholder in values.Keys.ToList())
            {
                var value = values[placeholder];
                if (value.Depth() > DynamoValueRules.MaxDepth)
                {
                    throw DynamoDbErrors.ValidationClause(DynamoValueRules.NestingExceeded);
                }

                if (!updating && value.Size() > DynamoValueRules.MaxItemBytes)
                {
                    // A value no item can hold: an update words it as the item it would grow.
                    throw DynamoDbErrors.Validation(expressions.Any(e => e.Member == "UpdateExpression" && e.Expression is not null)
                        ? DynamoValueRules.UpdateItemSizeExceeded
                        : DynamoValueRules.ItemSizeExceeded);
                }

                values[placeholder] = DynamoValueRules.Canonicalize(value, enveloped: expressions.Any(e => e.Expression is not null && ExpressionErrors.IsEnveloped(e.Member)));
            }
        }

        return attributes;
    }

    /// <summary>
    /// A read's expression attributes, checked against its expressions as <see cref="Attributes"/>
    /// checks them, except that a projection DynamoDB refuses is reported ahead of a placeholder
    /// the request doesn't use.
    /// </summary>
    private static ExpressionAttributes ReadAttributes(
        Dictionary<string, string>? names,
        Dictionary<string, DynamoValue>? values,
        string? projectionExpression,
        params (string Member, string? Expression)[] expressions)
    {
        try
        {
            return Attributes(names, values, [.. expressions, ("ProjectionExpression", projectionExpression)]);
        }
        catch (DynamoDbException unused) when (projectionExpression is not null && unused.Message.Contains("unused in expressions", StringComparison.Ordinal))
        {
            ExpressionParser.ParseProjection(projectionExpression, new ExpressionAttributes(names, values));
            throw;
        }
    }

    /// <summary>
    /// An item a write carries, held to DynamoDB's value rules and canonicalized; unless
    /// <paramref name="sized"/> is false, also refused when it is over 400 KB.
    /// </summary>
    private static Dictionary<string, DynamoValue> CanonicalItem(Dictionary<string, DynamoValue> item, bool sized = true)
    {
        if (DynamoValueRules.ExceedsDepth(item))
        {
            throw DynamoDbErrors.ValidationClause(DynamoValueRules.NestingExceeded);
        }

        var canonical = DynamoValueRules.CanonicalizeItem(item);
        if (sized)
        {
            CheckItemSize(canonical);
        }

        return canonical;
    }

    /// <summary>Refuses an item over 400 KB.</summary>
    private static void CheckItemSize(Dictionary<string, DynamoValue> item)
    {
        if (DynamoValueRules.ItemSize(item) > DynamoValueRules.MaxItemBytes)
        {
            throw DynamoDbErrors.Validation(DynamoValueRules.ItemSizeExceeded);
        }
    }

    /// <summary>A write's condition: its <c>ConditionExpression</c> or legacy <c>Expected</c>, or none.</summary>
    private static ExpressionCondition? WriteCondition(
        string? conditionExpression,
        Dictionary<string, ExpectedAttributeValue>? expected,
        string? conditionalOperator,
        ExpressionAttributes attributes)
    {
        if (conditionExpression is not null)
        {
            return ExpressionParser.ParseCondition("ConditionExpression", conditionExpression, attributes);
        }

        if (expected is { Count: > 0 })
        {
            LegacyConditions.CheckConditionalOperatorValue(conditionalOperator, "Expected");
            var values = expected.Values.SelectMany(e => e.AttributeValueList ?? []).Concat(expected.Values.Where(e => e.Value is not null).Select(e => e.Value!));
            if (values.Any(v => v.Depth() > DynamoValueRules.MaxDepth))
            {
                throw DynamoDbErrors.ValidationClause(DynamoValueRules.NestingExceeded);
            }

            foreach (var entry in expected.Values)
            {
                if (entry.Value is not null)
                {
                    entry.Value = DynamoValueRules.Canonicalize(entry.Value);
                }

                if (entry.AttributeValueList is not null)
                {
                    entry.AttributeValueList = entry.AttributeValueList.Select(v => DynamoValueRules.Canonicalize(v)).ToList();
                }
            }

            return LegacyConditions.FromExpected(expected, conditionalOperator);
        }

        LegacyConditions.CheckConditionalOperator(conditionalOperator, "Expected", 0);
        return null;
    }

    private static void CheckCondition(ExpressionCondition? condition, Dictionary<string, DynamoValue>? item, string? returnValuesOnConditionCheckFailure)
    {
        if (condition is not null && !condition.Evaluate(item))
        {
            throw new DynamoDbConditionalCheckFailedException(returnValuesOnConditionCheckFailure == "ALL_OLD" ? item : null);
        }
    }

    private static Dictionary<string, DynamoValue> Project(
        Dictionary<string, DynamoValue> item,
        IReadOnlyList<DocumentPath>? projection,
        List<string>? attributesToGet) =>
        projection is not null ? ItemProjection.Apply(item, projection)
        : attributesToGet is not null ? ItemProjection.Attributes(item, attributesToGet)
        : item;

    /// <summary>
    /// A key is held to the set rules before it is matched to the table's schema; a read states
    /// what it refuses bare, a write in the validation envelope.
    /// </summary>
    private static void CheckKeyValues(Dictionary<string, DynamoValue> key, bool enveloped)
    {
        foreach (var value in key.Values)
        {
            DynamoValueRules.CheckSetRules(value, enveloped);
        }
    }

    /// <summary>
    /// Refuses a key holding a number DynamoDB can't store. A transaction states this for the
    /// whole request rather than cancelling the element, so it is checked ahead of the element's
    /// schema checks.
    /// </summary>
    private static void CheckKeyNumbers(Dictionary<string, DynamoValue> key)
    {
        foreach (var value in key.Values)
        {
            if (value.Type == DynamoType.N)
            {
                DynamoValueRules.ParseNumber(value.N, enveloped: false);
            }
        }
    }

    /// <summary>
    /// The key a <c>Key</c> member names: exactly the table's key attributes, of their declared
    /// types. A read states a number's refusal bare, a write inside the validation envelope.
    /// </summary>
    private static DynamoKey KeyFromKeyMap(DynamoTable table, Dictionary<string, DynamoValue> key, bool enveloped = true)
    {
        // A set's own rules are checked before the key is matched to the schema.
        CheckKeyValues(key, enveloped);

        var schema = table.KeySchema;
        var expected = schema.Sort is null ? 1 : 2;
        if (key.Count != expected
            || !key.TryGetValue(schema.Partition.Name, out var partition)
            || partition.Type != schema.Partition.Type)
        {
            throw DynamoDbErrors.KeySchemaMismatch();
        }

        DynamoValue? sort = null;
        if (schema.Sort is not null && (!key.TryGetValue(schema.Sort.Name, out sort) || sort.Type != schema.Sort.Type))
        {
            throw DynamoDbErrors.KeySchemaMismatch();
        }

        return new DynamoKey(KeyValue(schema.Partition.Name, partition, MaxPartitionKeyBytes, enveloped), sort is null ? null : KeyValue(schema.Sort!.Name, sort, MaxSortKeyBytes, enveloped));
    }

    /// <summary>The primary key of an item a write carries: every key attribute present, of its declared type.</summary>
    private static DynamoKey KeyFromItem(DynamoTable table, Dictionary<string, DynamoValue> item, bool batch = false)
    {
        DynamoValue Attribute(DynamoKeyAttribute attribute, int maxBytes)
        {
            if (!item.TryGetValue(attribute.Name, out var value))
            {
                throw batch ? DynamoDbErrors.KeySchemaMismatch() : DynamoDbErrors.InvalidParameter($"Missing the key {attribute.Name} in the item");
            }

            if (value.Type != attribute.Type)
            {
                throw batch ? DynamoDbErrors.KeySchemaMismatch() : DynamoDbErrors.InvalidParameter($"Type mismatch for key {attribute.Name} expected: {attribute.Type} actual: {value.Type}");
            }

            return KeyValue(attribute.Name, value, maxBytes);
        }

        var schema = table.KeySchema;
        return new DynamoKey(Attribute(schema.Partition, MaxPartitionKeyBytes), schema.Sort is null ? null : Attribute(schema.Sort, MaxSortKeyBytes));
    }

    /// <summary>A key attribute's value: non-empty, a storable number, within the key size limit.</summary>
    private static DynamoValue KeyValue(string attribute, DynamoValue value, int maxBytes, bool enveloped = true)
    {
        CheckKeyValueNotEmpty(attribute, value);
        if (value.Type == DynamoType.N)
        {
            value = DynamoValueRules.Canonicalize(value, enveloped);
        }

        if (value.Size() > maxBytes)
        {
            throw DynamoDbErrors.InvalidParameter(maxBytes == MaxPartitionKeyBytes
                ? $"Size of hashkey has exceeded the maximum size limit of{MaxPartitionKeyBytes} bytes"
                : $"Aggregated size of all range keys has exceeded the size limit of {MaxSortKeyBytes} bytes");
        }

        return value;
    }

    /// <summary>A key attribute's value can't be an empty string or binary.</summary>
    private static void CheckKeyValueNotEmpty(string attribute, DynamoValue value)
    {
        if ((value.Type == DynamoType.S && value.S.Length == 0) || (value.Type == DynamoType.B && value.B.Length == 0))
        {
            throw DynamoDbErrors.Validation($"One or more parameter values are not valid. The AttributeValue for a key attribute cannot contain an empty {(value.Type == DynamoType.S ? "string" : "binary")} value. Key: {attribute}");
        }
    }

    /// <summary>Refuses an item whose secondary index key attribute has another type than the index declares, or is empty.</summary>
    private static void CheckIndexKeys(DynamoTable table, Dictionary<string, DynamoValue> item, bool update)
    {
        CheckIndexKeyTypes(table, item);
        CheckIndexKeyValues(table, item, update);
    }

    /// <summary>
    /// Refuses an item whose secondary index key attribute has another type than the index
    /// declares (global indexes are checked first), or that a vector index can't take.
    /// </summary>
    private static void CheckIndexKeyTypes(DynamoTable table, Dictionary<string, DynamoValue> item)
    {
        CheckIndexKeyTypesOnly(table, item);
        CheckVectorAttributes(table, item);
    }

    private static void CheckIndexKeyTypesOnly(DynamoTable table, Dictionary<string, DynamoValue> item)
    {
        foreach (var index in table.GlobalIndexes.Concat(table.LocalIndexes))
        {
            foreach (var attribute in index.KeySchema.Attributes)
            {
                if (!table.KeySchema.IsKeyAttribute(attribute.Name) && item.TryGetValue(attribute.Name, out var value) && value.Type != attribute.Type)
                {
                    throw DynamoDbErrors.InvalidParameter($"Type mismatch for Index Key {attribute.Name} Expected: {attribute.Type} Actual: {value.Type} IndexName: {index.Name}");
                }
            }
        }
    }

    /// <summary>Refuses an item whose secondary index key attribute (or vector index <c>HASH</c> attribute) is an empty string or binary.</summary>
    private static void CheckIndexKeyValues(DynamoTable table, Dictionary<string, DynamoValue> item, bool update)
    {
        var keyed = table.Indexes.Select(i => (i.Name, Attributes: i.KeySchema.Attributes.Select(a => a.Name)))
            .Concat(table.VectorIndexes.Select(v => (v.Name, Attributes: v.SearchSchema.Where(e => e.Type == DynamoSearchSchemaElement.Hash).Select(e => e.Name))));
        foreach (var index in keyed)
        {
            foreach (var attribute in index.Attributes)
            {
                if (table.KeySchema.IsKeyAttribute(attribute) || !item.TryGetValue(attribute, out var value))
                {
                    continue;
                }

                if ((value.Type == DynamoType.S && value.S.Length == 0) || (value.Type == DynamoType.B && value.B.Length == 0))
                {
                    var kind = value.Type == DynamoType.S ? "string" : "binary";
                    throw DynamoDbErrors.Validation(update
                        ? $"One or more parameter values are not valid. The update expression attempted to update a secondary index key to a value that is not supported. The AttributeValue for a key attribute cannot contain an empty {kind} value."
                        : $"One or more parameter values are not valid. A value specified for a secondary index key is not supported. The AttributeValue for a key attribute cannot contain an empty {kind} value. IndexName: {index.Name}, IndexKey: {attribute}");
                }
            }
        }
    }

    /// <summary>
    /// Refuses an item that, with its entry in a local secondary index, passes the item size limit:
    /// the limit holds for an item and each of its local index entries together.
    /// </summary>
    private static void CheckLocalIndexEntrySizes(DynamoTable table, Dictionary<string, DynamoValue> item, string message = DynamoValueRules.ItemSizeExceeded)
    {
        var size = DynamoValueRules.ItemSize(item);
        foreach (var index in table.LocalIndexes)
        {
            if (index.KeySchema.KeyOf(item) is not null && size + DynamoValueRules.ItemSize(ProjectForIndex(table, index, item)) > DynamoValueRules.MaxItemBytes)
            {
                throw DynamoDbErrors.Validation(message);
            }
        }
    }

    private static ItemCollectionMetrics? CollectionMetrics(string? returnItemCollectionMetrics, DynamoTable table, DynamoKey key)
    {
        if (returnItemCollectionMetrics != "SIZE" || table.LocalIndexes.Count == 0)
        {
            return null;
        }

        return new ItemCollectionMetrics
        {
            ItemCollectionKey = new Dictionary<string, DynamoValue>(StringComparer.Ordinal) { [table.KeySchema.Partition.Name] = key.Partition },
            SizeEstimateRangeGB = [0.0, 1.0],
        };
    }

    /// <summary>Hands a write to the table's stream, if it has one enabled, and to its replicas, if it has any.</summary>
    private void Changed(DynamoTable table, DynamoItemChange change)
    {
        var now = _cloud.TimeProvider.GetUtcNow();
        if (table.StreamEnabled && table.LatestStream is { Enabled: true } stream)
        {
            stream.Append(change, table, now);
        }

        table.Replication?.Replicate(table, change, now);
    }
}

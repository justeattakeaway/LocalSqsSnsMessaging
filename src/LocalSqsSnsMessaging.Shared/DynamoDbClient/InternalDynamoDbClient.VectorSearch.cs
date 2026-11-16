using LocalSqsSnsMessaging.DynamoDb.Expressions;
using LocalSqsSnsMessaging.DynamoDb.Model;

namespace LocalSqsSnsMessaging.DynamoDb;

/// <summary>
/// Vector indexes and <c>SearchVectors</c>: defining an index (CreateTable, UpdateTable), the
/// checks every write's vector attributes pass, what a write costs each index, and the search.
/// </summary>
internal sealed partial class InternalDynamoDbClient
{
    private const int MaxVectorIndexes = 5;
    private const int MaxTopK = 100;

    /// <summary>The smallest number of bytes a vector write or search is metered at.</summary>
    private const double MinVectorRequestBytes = 1024;

    private const string VectorIndexesNeedOnDemand = "Vector indexes are only supported for PAY_PER_REQUEST tables";

    private const string OneIndexAtATime = "Subscriber limit exceeded: Only 1 online index can be created or deleted simultaneously per table";

    /// <summary>
    /// A vector index as a CreateTable <c>VectorIndexes</c> element or an UpdateTable
    /// <c>Create</c> action defines it, checked against the attribute definitions its search
    /// schema resolves against.
    /// </summary>
    private static DynamoVectorIndex ParseVectorIndex(
        string tableArn,
        string? indexName,
        string? vectorAttribute,
        long? dimensions,
        string? distanceFunction,
        Model.Projection? projection,
        List<SearchSchemaElement>? searchSchema,
        IReadOnlyList<DynamoKeyAttribute> definitions)
    {
        if (dimensions is not (>= 1 and <= DynamoVectorIndex.MaxDimensions))
        {
            throw DynamoDbErrors.InvalidParameter($"Number of dimensions must be between 1 and {DynamoVectorIndex.MaxDimensions.ToString(CultureInfo.InvariantCulture)} inclusive.");
        }

        var elements = new List<DynamoSearchSchemaElement>();
        if (searchSchema is not null)
        {
            var hashes = searchSchema.Count(e => e.SearchSchemaElementType == DynamoSearchSchemaElement.Hash);
            if (hashes > 1)
            {
                throw DynamoDbErrors.InvalidParameter($"Value '{hashes.ToString(CultureInfo.InvariantCulture)}' at 'SearchSchema' failed to satisfy constraint: Member must have HASH count less than or equal to 1");
            }

            foreach (var element in searchSchema)
            {
                var definition = definitions.FirstOrDefault(d => string.Equals(d.Name, element.AttributeName, StringComparison.Ordinal))
                    ?? throw DynamoDbErrors.InvalidParameter("One element in SearchSchema is not defined in attribute definitions");
                if (elements.Any(e => string.Equals(e.Name, definition.Name, StringComparison.Ordinal)))
                {
                    throw DynamoDbErrors.InvalidParameter($"Duplicate value in attribute name: {definition.Name}");
                }

                elements.Add(new DynamoSearchSchemaElement(definition, element.SearchSchemaElementType!));
            }
        }

        return new DynamoVectorIndex
        {
            Name = indexName!,
            Arn = $"{tableArn}/index/{indexName}",
            VectorAttribute = vectorAttribute!,
            Dimensions = (int)dimensions.Value,
            DistanceFunction = distanceFunction!,
            Projection = ParseProjection(projection),
            SearchSchema = elements,
        };
    }

    /// <summary>
    /// Every vector index on one attribute must use the same dimensions: <paramref name="index"/>
    /// against the table's (or the request's) others.
    /// </summary>
    private static void CheckVectorDimensions(DynamoVectorIndex index, IEnumerable<DynamoVectorIndex> others)
    {
        if (others.Any(o => string.Equals(o.VectorAttribute, index.VectorAttribute, StringComparison.Ordinal) && o.Dimensions != index.Dimensions))
        {
            throw DynamoDbErrors.InvalidParameter($"Conflicting attribute definition for '{index.VectorAttribute}'. All VectorIndexes on the same vector attribute must use the same dimensions.");
        }
    }

    /// <summary>CreateTable's vector indexes, added to <paramref name="table"/>; the attributes their search schemas name are added to <paramref name="used"/>.</summary>
    private static void AddVectorIndexes(DynamoTable table, List<VectorIndex>? vectorIndexes, List<DynamoKeyAttribute> definitions, HashSet<string> indexNames, HashSet<string> used)
    {
        if (vectorIndexes is not { Count: > 0 })
        {
            return;
        }

        if (vectorIndexes.Count > MaxVectorIndexes)
        {
            throw DynamoDbErrors.InvalidParameter($"VectorIndex count exceeds the per-table limit of {MaxVectorIndexes.ToString(CultureInfo.InvariantCulture)}");
        }

        if (table.BillingMode != "PAY_PER_REQUEST")
        {
            throw DynamoDbErrors.InvalidParameter(VectorIndexesNeedOnDemand);
        }

        foreach (var definition in vectorIndexes)
        {
            if (!indexNames.Add(definition.IndexName!))
            {
                throw DynamoDbErrors.InvalidParameter($"Duplicate index name: {definition.IndexName}");
            }

            var index = ParseVectorIndex(
                table.Arn,
                definition.IndexName,
                definition.VectorAttribute?.AttributeName,
                definition.Dimensions,
                definition.DistanceFunction,
                definition.Projection,
                definition.SearchSchema,
                definitions);
            CheckVectorDimensions(index, table.VectorIndexes);
            used.UnionWith(index.SearchSchema.Select(e => e.Name));
            table.VectorIndexes.Add(index);
        }
    }

    /// <summary>At most one index, secondary or vector, is created or deleted by an UpdateTable.</summary>
    private static void CheckVectorIndexUpdates(List<VectorIndexUpdate>? updates, List<GlobalSecondaryIndexUpdate>? indexUpdates)
    {
        var builds = (updates?.Count ?? 0) + (indexUpdates?.Count(u => u.Create is not null || u.Delete is not null) ?? 0);
        if (updates is { Count: > 0 } && builds > 1)
        {
            throw DynamoDbErrors.LimitExceeded(OneIndexAtATime);
        }
    }

    /// <summary>
    /// Checks a vector index update against the table as it stands, under the table's
    /// <paramref name="billingMode"/> once this request applies, and returns the change to make
    /// and, for a delete, how the response describes the index it removes.
    /// </summary>
    private (Action Apply, VectorIndexDescription? Deleted) PrepareVectorIndexUpdate(
        DynamoTable table,
        VectorIndexUpdate update,
        List<AttributeDefinition>? definitions,
        string billingMode)
    {
        if (update.Create is { } create)
        {
            if (billingMode != "PAY_PER_REQUEST")
            {
                throw DynamoDbErrors.InvalidParameter(VectorIndexesNeedOnDemand);
            }

            if (table.HasIndexNamed(create.IndexName!))
            {
                throw DynamoDbErrors.Validation("Attempting to create an index which already exists");
            }

            if (table.VectorIndexes.Any(v => v.Status == "CREATING") || table.GlobalIndexes.Any(i => i.Status == "CREATING"))
            {
                throw DynamoDbErrors.LimitExceeded(OneIndexAtATime);
            }

            if (table.VectorIndexes.Count >= MaxVectorIndexes)
            {
                throw DynamoDbErrors.InvalidParameter($"VectorIndex count exceeds the per-table limit of {MaxVectorIndexes.ToString(CultureInfo.InvariantCulture)}");
            }

            // The search schema resolves against the table's definitions and the request's.
            var known = table.AttributeDefinitions.ToList();
            foreach (var definition in definitions ?? [])
            {
                if (table.DeclaredType(definition.AttributeName!) is null)
                {
                    known.Add(new DynamoKeyAttribute(definition.AttributeName!, ParseScalarType(definition.AttributeType)));
                }
            }

            var index = ParseVectorIndex(table.Arn, create.IndexName, create.VectorAttribute?.AttributeName, create.Dimensions, create.DistanceFunction, create.Projection, create.SearchSchema, known);
            CheckVectorDimensions(index, table.VectorIndexes);
            return (() =>
            {
                foreach (var element in index.SearchSchema)
                {
                    if (table.DeclaredType(element.Name) is null)
                    {
                        table.AttributeDefinitions.Add(element.Attribute);
                    }
                }

                var delay = _bus.DynamoDbVectorIndexCreationDelay;
                if (delay > TimeSpan.Zero)
                {
                    var now = _bus.TimeProvider.GetUtcNow();
                    index.Status = "CREATING";
                    index.BackfillingAt = now + TimeSpan.FromTicks(delay.Ticks / 2);
                    index.ActiveAt = now + delay;
                }

                table.VectorIndexes.Add(index);
                table.Backfill(index);
            }, null);
        }

        if (update.Delete is { } delete)
        {
            var index = table.FindVectorIndex(delete.IndexName!)
                ?? throw DynamoDbErrors.ResourceNotFound($"Requested resource not found: Index {delete.IndexName} for table {table.Name}");
            if (index.Allocating)
            {
                throw DynamoDbErrors.ResourceInUse($"Attempt to change a resource which is still in use: Index creation is in resource allocation phase. Retry deletion during backfilling phase or when the index is active. Table: {table.Name} Index: {index.Name}");
            }

            if (table.VectorIndexes.Any(v => v != index && v.Status == "CREATING") || table.GlobalIndexes.Any(i => i.Status == "CREATING"))
            {
                throw DynamoDbErrors.LimitExceeded(OneIndexAtATime);
            }

            var deleted = DescribeVectorIndex(index);
            deleted.IndexStatus = "DELETING";
            deleted.Backfilling = null;
            return (() =>
            {
                table.VectorIndexes.Remove(index);
                var stillUsed = table.UsedAttributeNames();
                table.AttributeDefinitions.RemoveAll(a => !stillUsed.Contains(a.Name));
            }, deleted);
        }

        return (() => { }, null);
    }

    /// <summary>Moves the table's <c>CREATING</c> vector indexes on as their times come.</summary>
    private void RefreshVectorIndexes(DynamoTable table)
    {
        if (table.VectorIndexes.Count == 0)
        {
            return;
        }

        var now = _bus.TimeProvider.GetUtcNow();
        foreach (var index in table.VectorIndexes)
        {
            index.Refresh(now);
        }
    }

    private static VectorIndexDescription DescribeVectorIndex(DynamoVectorIndex index) => new()
    {
        IndexName = index.Name,
        SearchSchema = index.SearchSchema.Count == 0
            ? null
            : index.SearchSchema.Select(e => new SearchSchemaElement { AttributeName = e.Name, SearchSchemaElementType = e.Type }).ToList(),
        Projection = ProjectionModel(index.Projection),
        VectorAttribute = new VectorAttributeDefinition { AttributeName = index.VectorAttribute },
        Dimensions = index.Dimensions,
        DistanceFunction = index.DistanceFunction,
        IndexStatus = index.Status,
        Backfilling = index.Backfilling ? true : null,
        IndexSizeBytes = index.SizeBytes,
        ItemCount = index.ItemCount,
        IndexArn = index.Arn,
    };

    // Writes

    /// <summary>
    /// The canonical item a write carries, with each vector attribute as the request wrote it:
    /// DynamoDB keeps a vector's numbers as written (<c>1.0</c> reads back <c>1.0</c>, where any
    /// other number reads back <c>1</c>), once they have passed the number rules like any other.
    /// </summary>
    private static Dictionary<string, DynamoValue> WithVectorsAsWritten(DynamoTable table, Dictionary<string, DynamoValue> canonical, Dictionary<string, DynamoValue> written)
    {
        var replaced = false;
        foreach (var index in table.VectorIndexes)
        {
            if (written.TryGetValue(index.VectorAttribute, out var value) && value.Type == DynamoType.L && canonical.ContainsKey(index.VectorAttribute))
            {
                canonical[index.VectorAttribute] = value;
                replaced = true;
            }
        }

        if (replaced)
        {
            DynamoValueRules.CheckItemLimits(canonical);
        }

        return canonical;
    }

    /// <summary>
    /// Refuses an item whose vector attribute some vector index can't take: not a list, a list of
    /// another length than the index's dimensions, or holding something other than a 32-bit float.
    /// </summary>
    private static void CheckVectorAttributes(DynamoTable table, Dictionary<string, DynamoValue> item)
    {
        foreach (var index in table.VectorIndexes)
        {
            if (index.Refusal(item) is { } refusal)
            {
                throw DynamoDbErrors.Validation(refusal);
            }

            foreach (var element in index.SearchSchema)
            {
                if (!table.KeySchema.IsKeyAttribute(element.Name) && item.TryGetValue(element.Name, out var value) && value.Type != element.Attribute.Type)
                {
                    throw DynamoDbErrors.InvalidParameter($"Type mismatch for Index Key {element.Name} Expected: {element.Attribute.Type} Actual: {value.Type} IndexName: {index.Name}");
                }
            }
        }
    }

    /// <summary>
    /// What an item write costs each vector index whose entry it changes: the index's
    /// <c>VectorWriteRequestBytes</c>, at least <see cref="MinVectorRequestBytes"/>. An index whose
    /// entry (vector and projected attributes) the write leaves as it was costs nothing. Null when
    /// no index is charged.
    /// </summary>
    private static Dictionary<string, double>? VectorWriteBytes(DynamoTable table, IReadOnlyDictionary<string, DynamoValue>? before, IReadOnlyDictionary<string, DynamoValue>? after)
    {
        Dictionary<string, double>? charged = null;
        foreach (var index in table.VectorIndexes)
        {
            var oldEntry = before is not null && index.HoldsVector(before) ? index.Entry(table.KeySchema, before) : null;
            var newEntry = after is not null && index.HoldsVector(after) ? index.Entry(table.KeySchema, after) : null;
            if (oldEntry is null && newEntry is null)
            {
                continue;
            }

            if (oldEntry is not null && newEntry is not null && SameEntry(oldEntry, newEntry))
            {
                continue;
            }

            var bytes = table.BilledSize(newEntry ?? oldEntry);
            (charged ??= new Dictionary<string, double>(StringComparer.Ordinal))[index.Name] = Math.Max(MinVectorRequestBytes, bytes);
        }

        return charged;
    }

    /// <summary>Adds what an item write costs each vector index to a running per-table, per-index total.</summary>
    private static void AddVectorWriteBytes(
        Dictionary<DynamoTable, Dictionary<string, double>> totals,
        DynamoTable table,
        IReadOnlyDictionary<string, DynamoValue>? before,
        IReadOnlyDictionary<string, DynamoValue>? after)
    {
        if (VectorWriteBytes(table, before, after) is not { } charged)
        {
            return;
        }

        if (!totals.TryGetValue(table, out var sums))
        {
            totals[table] = sums = new Dictionary<string, double>(StringComparer.Ordinal);
        }

        foreach (var (index, bytes) in charged)
        {
            sums[index] = (sums.TryGetValue(index, out var sum) ? sum : 0) + bytes;
        }
    }

    // SearchVectors

    public Task<SearchVectorsResponse> SearchVectorsAsync(SearchVectorsRequest request, CancellationToken cancellationToken = default)
    {
        var name = TableName(request.TableName!);
        if (request.TopK is > MaxTopK)
        {
            throw DynamoDbErrors.Validation($"Provided TopK value '{request.TopK.Value.ToString(CultureInfo.InvariantCulture)}' is out of valid range. The value must be between 1 and {MaxTopK.ToString(CultureInfo.InvariantCulture)} inclusive");
        }

        var attributes = ReadAttributes(
            request.ExpressionAttributeNames,
            request.ExpressionAttributeValues,
            request.ProjectionExpression,
            ("SearchConditionExpression", request.SearchConditionExpression));
        var projection = request.ProjectionExpression is null ? null : ExpressionParser.ParseProjection(request.ProjectionExpression, attributes);
        var condition = request.SearchConditionExpression is null
            ? null
            : ExpressionParser.ParseCondition("SearchConditionExpression", request.SearchConditionExpression, attributes);
        var query = SearchVector(request.SearchVector!);

        lock (Store.Gate)
        {
            var table = ActiveTable(name);
            Authorize(table, "SearchVectors", IndexArn(table, request.IndexName));

            // An index still being built can't be searched: it is answered as no index at all.
            var index = table.FindVectorIndex(request.IndexName!) is { Status: "ACTIVE" } found
                ? found
                : throw DynamoDbErrors.Validation($"The table does not have the specified index: {request.IndexName}");
            if (query.Length != index.Dimensions)
            {
                throw DynamoDbErrors.Validation($"Input search vector dimension {query.Length.ToString(CultureInfo.InvariantCulture)} does not match vector index dimension {index.Dimensions.ToString(CultureInfo.InvariantCulture)}");
            }

            CheckSearchCondition(index, condition);

            var lowerIsCloser = index.LowerIsCloser;
            var scored = new List<(DynamoKey Key, Dictionary<string, DynamoValue> Item, float Score)>();
            foreach (var (key, vector) in index.ReadVectors())
            {
                var item = table.Get(key)!;
                if (condition is null || condition.Evaluate(item))
                {
                    scored.Add((key, item, index.Score(query, vector)));
                }
            }

            var nearest = scored
                .OrderBy(s => lowerIsCloser ? s.Score : -s.Score)
                .ThenBy(s => s.Key, DynamoKey.Comparer)
                .Take(request.TopK ?? MaxTopK)
                .ToList();

            var results = new List<SearchResultItem>(nearest.Count);
            var bytes = 4.0 * index.Dimensions;
            foreach (var (_, item, score) in nearest)
            {
                // The index's entry, without the vector unless the projection asks for it.
                var entry = index.Entry(table.KeySchema, item);
                var returned = projection is null
                    ? entry.Where(e => !string.Equals(e.Key, index.VectorAttribute, StringComparison.Ordinal)).ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal)
                    : ItemProjection.Apply(entry, projection);
                bytes += table.BilledSize(returned);
                results.Add(new SearchResultItem { Item = returned, Score = score });
            }

            Record("SearchVectors", table.Arn);
            return Task.FromResult(new SearchVectorsResponse
            {
                SearchResults = results,
                ConsumedCapacity = request.ReturnConsumedCapacity is "TOTAL" or "INDEXES"
                    ? new VectorCapacity { VectorSearchRequestBytes = Math.Max(MinVectorRequestBytes, bytes) }
                    : null,
            });
        }
    }

    /// <summary>The query vector as 32-bit floats; every element must be a number one can hold.</summary>
    private static float[] SearchVector(List<DynamoValue> values)
    {
        var vector = new float[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i].Type != DynamoType.N || !DynamoVectorIndex.TryFloat(values[i].N, out vector[i]))
            {
                throw DynamoDbErrors.Validation("Search vector contains invalid values. All values in the search vector must be a 32-bit floating-point number attribute");
            }
        }

        return vector;
    }

    /// <summary>
    /// A search condition may only compare the index's search schema attributes for equality,
    /// joined by AND, and must name the <c>HASH</c> attribute when the index has one.
    /// </summary>
    private static void CheckSearchCondition(DynamoVectorIndex index, ExpressionCondition? condition)
    {
        const string HashRequired = "SearchConditionExpression must be provided when SearchSchema has a HASH key";
        if (condition is null)
        {
            if (index.HashElement is not null)
            {
                throw DynamoDbErrors.Validation(HashRequired);
            }

            return;
        }

        var compared = new List<string>();
        void Check(ExpressionCondition node)
        {
            switch (node)
            {
                case AndCondition and:
                    foreach (var inner in and.Conditions)
                    {
                        Check(inner);
                    }

                    break;
                case CompareCondition compare:
                    foreach (var operand in new[] { compare.Left, compare.Right })
                    {
                        if (operand is PathOperand path)
                        {
                            var attribute = path.Path.IsTopLevel ? path.Path.AttributeName : path.Path.Render();
                            if (index.SchemaElement(attribute) is null)
                            {
                                throw DynamoDbErrors.Validation($"SearchConditionExpression must not contain any attributes that is not in SearchSchema. Invalid attribute: {attribute}");
                            }

                            compared.Add(attribute);
                        }
                    }

                    // An attribute equal to a value: one operand a path, the other not.
                    if (compare.Operator != CompareOperator.Equal || (compare.Left is PathOperand) == (compare.Right is PathOperand))
                    {
                        throw InvalidComparator();
                    }

                    break;
                default:
                    throw InvalidComparator();
            }
        }

        Check(condition);
        if (index.HashElement is { } hash && !compared.Contains(hash.Name, StringComparer.Ordinal))
        {
            throw DynamoDbErrors.Validation(HashRequired);
        }

        static DynamoDbException InvalidComparator() =>
            DynamoDbErrors.Validation("Invalid SearchConditionExpression: Invalid comparator used in SearchConditionExpression");
    }
}

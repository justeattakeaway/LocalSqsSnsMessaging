using LocalSqsSnsMessaging.DynamoDb.Expressions;
using LocalSqsSnsMessaging.DynamoDb.Model;

namespace LocalSqsSnsMessaging.DynamoDb;

/// <summary>Query and Scan, of a table or one of its secondary indexes.</summary>
internal sealed partial class InternalDynamoDbClient
{
    /// <summary>A Query or Scan stops once it has read this much, and answers with a LastEvaluatedKey.</summary>
    private const long MaxPageBytes = 1024 * 1024;

    /// <summary>What a Query or Scan asks for, gathered from either.</summary>
    private sealed class ReadPlan
    {
        public required DynamoTable Table { get; init; }

        public DynamoIndex? Index { get; init; }

        public required string Select { get; init; }

        public IReadOnlyList<DocumentPath>? Projection { get; init; }

        public List<string>? AttributesToGet { get; init; }

        public ExpressionCondition? Filter { get; init; }

        public int? Limit { get; init; }

        public bool Consistent { get; init; }

        public string? ReturnConsumedCapacity { get; init; }

        /// <summary>The key schema the read is ordered by: the index's, or the table's.</summary>
        public DynamoKeySchema OrderSchema => Index?.KeySchema ?? Table.KeySchema;
    }

    public Task<QueryResponse> QueryAsync(QueryRequest request, CancellationToken cancellationToken = default)
    {
        var name = TableName(request.TableName!);
        LegacyConditions.CheckNoMix(
            [
                ("AttributesToGet", request.AttributesToGet is not null),
                ("QueryFilter", request.QueryFilter is { Count: > 0 }),
                ("ConditionalOperator", request.ConditionalOperator is not null),

                // KeyConditions is replaced only by KeyConditionExpression: it may go with a filter or projection expression.
                ("KeyConditions", request.KeyConditions is { Count: > 0 } && request.KeyConditionExpression is not null),
            ],
            [
                ("ProjectionExpression", request.ProjectionExpression is not null),
                ("FilterExpression", request.FilterExpression is not null),
                ("KeyConditionExpression", request.KeyConditionExpression is not null),
            ]);
        if (request.KeyConditionExpression is not null)
        {
            KeyCondition.CheckOperandsBeforeUndefined(request.KeyConditionExpression, new ExpressionAttributes(request.ExpressionAttributeNames, request.ExpressionAttributeValues));
        }

        var attributes = ReadAttributes(
            request.ExpressionAttributeNames,
            request.ExpressionAttributeValues,
            request.ProjectionExpression,
            ("KeyConditionExpression", request.KeyConditionExpression),
            ("FilterExpression", request.FilterExpression));

        if (request.FilterExpression is null)
        {
            // A ConditionalOperator with too few QueryFilter conditions is refused before the key condition is looked for.
            LegacyConditions.CheckConditionalOperator(request.ConditionalOperator, "QueryFilter", request.QueryFilter?.Count ?? 0);
        }

        List<KeyComparison> comparisons;
        if (request.KeyConditionExpression is not null)
        {
            comparisons = KeyCondition.Parse(request.KeyConditionExpression, attributes);
        }
        else if (request.KeyConditions is { Count: > 0 } keyConditions)
        {
            comparisons = KeyCondition.FromLegacy(keyConditions);
        }
        else
        {
            throw DynamoDbErrors.Validation(KeyCondition.Required);
        }

        var filter = ReadFilter("FilterExpression", request.FilterExpression, "QueryFilter", request.QueryFilter, request.ConditionalOperator, attributes);
        var projection = request.ProjectionExpression is null ? null : ExpressionParser.ParseProjection(request.ProjectionExpression, attributes);
        CheckAttributesToGet(request.AttributesToGet);

        lock (Store.Gate)
        {
            var plan = Plan("Query", name, request.IndexName, request.Select, projection, request.AttributesToGet, filter, request.Limit, request.ConsistentRead, request.ReturnConsumedCapacity, request.FilterExpression ?? (request.QueryFilter is { Count: > 0 } ? "QueryFilter" : null));
            var keyCondition = KeyCondition.Resolve(comparisons, plan.OrderSchema, plan.Table.DeclaredType);
            CheckFilterHasNoKeys(plan, filter);
            CheckFilterProjected(plan, filter);
            var start = StartKey(plan, request.ExclusiveStartKey, scan: false);
            if (start is not null && !DynamoKeyOrder.Compare(start.Order.Partition, keyCondition.Partition).Equals(0))
            {
                throw DynamoDbErrors.Validation("The provided starting key is outside query boundaries based on provided conditions");
            }

            if (start is not null && !keyCondition.MatchesSort(plan.OrderSchema.Sort is null ? null : start.Order.Sort))
            {
                throw DynamoDbErrors.Validation("The provided starting key does not match the range key predicate");
            }

            var forward = request.ScanIndexForward ?? true;
            var candidates = Partition(plan, keyCondition, forward, start)
                .Where(c => keyCondition.MatchesSort(plan.OrderSchema.Sort is null ? null : c.OrderKey.Sort));
            var result = Read(plan, candidates);
            Record("Query", plan.Table.Arn);
            return Task.FromResult(new QueryResponse
            {
                Items = result.Items,
                Count = result.Count,
                ScannedCount = result.ScannedCount,
                LastEvaluatedKey = result.LastEvaluatedKey,
                ConsumedCapacity = result.ConsumedCapacity,
            });
        }
    }

    public Task<ScanResponse> ScanAsync(ScanRequest request, CancellationToken cancellationToken = default)
    {
        var name = TableName(request.TableName!);
        if (request.Segment is not null && request.TotalSegments is null)
        {
            throw DynamoDbErrors.Validation("The TotalSegments parameter is required but was not present in the request when Segment parameter is present");
        }

        if (request.TotalSegments is not null && request.Segment is null)
        {
            throw DynamoDbErrors.Validation("The Segment parameter is required but was not present in the request when parameter TotalSegments is present");
        }

        if (request.Segment is { } segment && segment >= request.TotalSegments)
        {
            throw DynamoDbErrors.Validation("The Segment parameter is zero-based and must be less than parameter TotalSegments");
        }

        LegacyConditions.CheckNoMix(
            [
                ("AttributesToGet", request.AttributesToGet is not null),
                ("ScanFilter", request.ScanFilter is { Count: > 0 }),
                ("ConditionalOperator", request.ConditionalOperator is not null),
            ],
            [
                ("ProjectionExpression", request.ProjectionExpression is not null),
                ("FilterExpression", request.FilterExpression is not null),
            ]);
        var attributes = ReadAttributes(
            request.ExpressionAttributeNames,
            request.ExpressionAttributeValues,
            request.ProjectionExpression,
            ("FilterExpression", request.FilterExpression));
        var filter = ReadFilter("FilterExpression", request.FilterExpression, "ScanFilter", request.ScanFilter, request.ConditionalOperator, attributes);
        var projection = request.ProjectionExpression is null ? null : ExpressionParser.ParseProjection(request.ProjectionExpression, attributes);
        CheckAttributesToGet(request.AttributesToGet);

        lock (Store.Gate)
        {
            var plan = Plan("Scan", name, request.IndexName, request.Select, projection, request.AttributesToGet, filter, request.Limit, request.ConsistentRead, request.ReturnConsumedCapacity, request.FilterExpression ?? (request.ScanFilter is { Count: > 0 } ? "ScanFilter" : null));
            var start = StartKey(plan, request.ExclusiveStartKey, scan: true);
            var candidates = All(plan, start);
            if (request.Segment is { } wanted)
            {
                var total = request.TotalSegments!.Value;
                candidates = candidates.Where(c => SegmentOf(c.TableKey.Partition, total) == wanted);
            }

            var result = Read(plan, candidates);
            Record("Scan", plan.Table.Arn);
            return Task.FromResult(new ScanResponse
            {
                Items = result.Items,
                Count = result.Count,
                ScannedCount = result.ScannedCount,
                LastEvaluatedKey = result.LastEvaluatedKey,
                ConsumedCapacity = result.ConsumedCapacity,
            });
        }
    }

    /// <summary>The segment of a parallel scan an item falls in, from a stable hash of its partition key.</summary>
    private static int SegmentOf(DynamoValue partition, int totalSegments)
    {
        var text = partition.Type switch
        {
            DynamoType.S => "S" + partition.S,
            DynamoType.N => "N" + partition.N,
            _ => "B" + Convert.ToBase64String(partition.B),
        };
        var hash = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            hash = unchecked((hash ^ b) * 16777619u);
        }

        return (int)(hash % (uint)totalSegments);
    }

    private static ExpressionCondition? ReadFilter(
        string expressionMember,
        string? expression,
        string legacyMember,
        Dictionary<string, Model.Condition>? legacy,
        string? conditionalOperator,
        ExpressionAttributes attributes)
    {
        if (expression is not null)
        {
            return ExpressionParser.ParseCondition(expressionMember, expression, attributes);
        }

        if (legacy is { Count: > 0 })
        {
            foreach (var condition in legacy.Values)
            {
                if (condition.AttributeValueList is not null)
                {
                    condition.AttributeValueList = condition.AttributeValueList.Select(v => DynamoValueRules.Canonicalize(v, enveloped: false)).ToList();
                }
            }

            return LegacyConditions.FromFilter(legacyMember, legacy, conditionalOperator);
        }

        LegacyConditions.CheckConditionalOperator(conditionalOperator, legacyMember, 0);
        return null;
    }

    /// <param name="action">The operation, <c>Query</c> or <c>Scan</c>, as the table's policy names it.</param>
    private ReadPlan Plan(
        string action,
        string tableName,
        string? indexName,
        string? select,
        IReadOnlyList<DocumentPath>? projection,
        List<string>? attributesToGet,
        ExpressionCondition? filter,
        int? limit,
        bool? consistentRead,
        string? returnConsumedCapacity,
        string? filterMember)
    {
        var table = ActiveTable(tableName);

        // A read of an index acts on the index's ARN, under the table's policy, whether or not the index exists.
        Authorize(table, action, IndexArn(table, indexName));
        DynamoIndex? index = null;
        if (indexName is not null)
        {
            index = table.FindIndex(indexName) ?? throw DynamoDbErrors.Validation($"The table does not have the specified index: {indexName}");
            if (index.IsGlobal && consistentRead == true)
            {
                throw DynamoDbErrors.Validation("Consistent reads are not supported on global secondary indexes");
            }
        }

        var specific = projection is not null || attributesToGet is not null;
        var chosen = select ?? (specific ? "SPECIFIC_ATTRIBUTES" : index is not null ? "ALL_PROJECTED_ATTRIBUTES" : "ALL_ATTRIBUTES");
        if (specific && chosen != "SPECIFIC_ATTRIBUTES")
        {
            throw DynamoDbErrors.ValidationClause($"Cannot specify the {(projection is not null ? "ProjectionExpression" : "AttributesToGet")} when choosing to get {(chosen == "COUNT" ? "only the Count" : chosen)}");
        }

        if (chosen == "SPECIFIC_ATTRIBUTES" && !specific)
        {
            throw DynamoDbErrors.ValidationClause("Must specify the AttributesToGet or ProjectionExpression when choosing to get SPECIFIC_ATTRIBUTES");
        }

        if (chosen == "ALL_PROJECTED_ATTRIBUTES" && index is null)
        {
            throw DynamoDbErrors.ValidationClause("ALL_PROJECTED_ATTRIBUTES can be used only when Querying using an IndexName");
        }

        if (index is { IsGlobal: true })
        {
            if (chosen == "ALL_ATTRIBUTES" && index.Projection.Type != "ALL")
            {
                throw DynamoDbErrors.InvalidParameter($"Select type ALL_ATTRIBUTES is not supported for global secondary index {index.Name} because its projection type is not ALL");
            }

            var requested = projection?.Select(p => p.AttributeName) ?? attributesToGet ?? Enumerable.Empty<string>();
            var missing = requested.Where(a => !IndexProjects(table, index, a)).Distinct(StringComparer.Ordinal).ToList();
            if (missing.Count > 0)
            {
                throw DynamoDbErrors.InvalidParameter($"Global secondary index {index.Name} does not project [{string.Join(", ", missing)}]");
            }
        }

        _ = filterMember;
        return new ReadPlan
        {
            Table = table,
            Index = index,
            Select = chosen,
            Projection = projection,
            AttributesToGet = attributesToGet,
            Filter = filter,
            Limit = limit,
            Consistent = consistentRead ?? false,
            ReturnConsumedCapacity = returnConsumedCapacity,
        };
    }

    /// <summary>
    /// Whether a local index read fetches each item from the table: "If any of the requested
    /// attributes are not projected into the local secondary index, DynamoDB fetches each of
    /// these attributes from the parent table", which a filter's attributes need as well.
    /// </summary>
    private static bool FetchesFromTable(ReadPlan plan)
    {
        if (plan.Index is not { IsGlobal: false } index || index.Projection.Type == "ALL" || plan.Select == "COUNT" && plan.Filter is null)
        {
            return false;
        }

        var needed = (plan.Projection?.Select(p => p.AttributeName) ?? plan.AttributesToGet ?? Enumerable.Empty<string>())
            .Concat(plan.Filter is null ? [] : FilterAttributes(plan.Filter));
        return plan.Select == "ALL_ATTRIBUTES" || needed.Any(a => !IndexProjects(plan.Table, index, a));
    }

    /// <summary>The top-level attributes a filter reads.</summary>
    private static IEnumerable<string> FilterAttributes(ExpressionCondition condition)
    {
        IEnumerable<string> Operand(Operand operand) => operand switch
        {
            PathOperand path => [path.Path.AttributeName],
            SizeOperand size => [size.Path.AttributeName],
            _ => [],
        };

        return condition switch
        {
            CompareCondition c => Operand(c.Left).Concat(Operand(c.Right)),
            BetweenCondition b => Operand(b.Value).Concat(Operand(b.Low)).Concat(Operand(b.High)),
            InCondition i => Operand(i.Value).Concat(i.Candidates.SelectMany(Operand)),
            ExistsCondition e => [e.Path.AttributeName],
            AttributeTypeCondition t => [t.Path.AttributeName],
            BeginsWithCondition b => Operand(b.Value).Concat(Operand(b.Prefix)),
            ContainsCondition c => Operand(c.Haystack).Concat(Operand(c.Needle)),
            LegacyComparison l => [l.Attribute],
            _ => condition.Children.SelectMany(FilterAttributes),
        };
    }

    /// <summary>
    /// A Query of a secondary index can't filter on an attribute the index doesn't project (a
    /// Scan of it can, and the attribute is simply never there).
    /// </summary>
    private static void CheckFilterProjected(ReadPlan plan, ExpressionCondition? filter)
    {
        if (plan.Index is not { } index || filter is null || index.Projection.Type == "ALL")
        {
            return;
        }

        var missing = FilterAttributes(filter).Where(a => !IndexProjects(plan.Table, index, a)).Distinct(StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
        {
            throw DynamoDbErrors.InvalidParameter($"Secondary index {index.Name} does not project one or more filter attributes: [{string.Join(", ", missing)}]");
        }
    }

    /// <summary>A Query's filter can't name the key attributes of what it queries.</summary>
    private static void CheckFilterHasNoKeys(ReadPlan plan, ExpressionCondition? filter)
    {
        if (filter is null)
        {
            return;
        }

        var keys = plan.OrderSchema.Attributes.Select(a => a.Name).ToList();
        foreach (var attribute in FilterAttributes(filter))
        {
            if (keys.Contains(attribute, StringComparer.Ordinal))
            {
                throw DynamoDbErrors.Validation($"Filter Expression can only contain non-primary key attributes: Primary key attribute: {attribute}");
            }
        }
    }

    /// <summary>One item a read considers: its key in the read's order, its table key, and the item.</summary>
    private readonly record struct Candidate(DynamoKey OrderKey, DynamoKey TableKey, Dictionary<string, DynamoValue> Item);

    /// <summary>Where an ExclusiveStartKey resumes: the read-order key and table key it names.</summary>
    private sealed record ResumePoint(DynamoKey Order, DynamoKey Table);

    private static ResumePoint? StartKey(ReadPlan plan, Dictionary<string, DynamoValue>? start, bool scan)
    {
        if (start is null)
        {
            return null;
        }

        var expected = plan.Table.KeySchema.Attributes.Concat(plan.Index?.KeySchema.Attributes ?? []).Select(a => a.Name).Distinct(StringComparer.Ordinal).ToList();
        if (start.Count != expected.Count || !expected.All(start.ContainsKey))
        {
            throw DynamoDbErrors.Validation("The provided starting key is invalid");
        }

        // An empty key value: a Scan of a table with secondary indexes words it as an index key's.
        var firstIndex = plan.Table.GlobalIndexes.Concat(plan.Table.LocalIndexes).FirstOrDefault();
        foreach (var name in expected)
        {
            var value = start[name];
            if ((value.Type == DynamoType.S && value.S.Length == 0) || (value.Type == DynamoType.B && value.B.Length == 0))
            {
                if (scan && firstIndex is not null)
                {
                    throw DynamoDbErrors.Validation($"One or more parameter values are not valid. A value specified for a secondary index key is not supported. The AttributeValue for a key attribute cannot contain an empty {(value.Type == DynamoType.S ? "string" : "binary")} value. IndexName: {firstIndex.Name}, IndexKey: {name}");
                }

                CheckKeyValueNotEmpty(name, value);
            }
        }

        var canonical = DynamoValueRules.CanonicalizeItem(start, enveloped: false);
        var table = plan.Table.KeySchema.KeyOf(canonical);
        var order = plan.Index is null ? table : plan.Index.KeySchema.KeyOf(canonical);
        if (table is null || order is null)
        {
            throw DynamoDbErrors.Validation("The provided starting key is invalid: The provided key element does not match the schema");
        }

        return new ResumePoint(order, table);
    }

    private static IEnumerable<Candidate> All(ReadPlan plan, ResumePoint? start)
    {
        if (plan.Index is null)
        {
            var keys = start is null ? plan.Table.Keys : plan.Table.Keys.Count == 0 || DynamoKey.Comparer.Compare(start.Table, plan.Table.Keys.Max!) >= 0
                ? Enumerable.Empty<DynamoKey>()
                : plan.Table.Keys.GetViewBetween(start.Table, plan.Table.Keys.Max!).Where(k => !k.Equals(start.Table));
            return keys.ToList().Select(k => new Candidate(k, k, plan.Table.Get(k)!));
        }

        IEnumerable<DynamoIndexEntry> entries = plan.Index.Entries;
        if (start is not null)
        {
            var from = new DynamoIndexEntry(start.Order, start.Table);
            entries = entries.Where(e => DynamoIndexEntry.Comparer.Compare(e, from) > 0);
        }

        return entries.ToList().Select(e => new Candidate(e.IndexKey, e.TableKey, plan.Table.Get(e.TableKey)!));
    }

    private static List<Candidate> Partition(ReadPlan plan, KeyCondition condition, bool forward, ResumePoint? start)
    {
        List<Candidate> candidates;
        if (plan.Index is null)
        {
            var keys = plan.Table.Keys.GetViewBetween(DynamoKey.PartitionStart(condition.Partition), DynamoKey.PartitionEnd(condition.Partition));
            candidates = keys.Select(k => new Candidate(k, k, plan.Table.Get(k)!)).ToList();
        }
        else
        {
            var entries = plan.Index.Entries.GetViewBetween(
                DynamoIndexEntry.Bound(DynamoKey.PartitionStart(condition.Partition)),
                DynamoIndexEntry.Bound(DynamoKey.PartitionEnd(condition.Partition)));
            candidates = entries.Select(e => new Candidate(e.IndexKey, e.TableKey, plan.Table.Get(e.TableKey)!)).ToList();
        }

        if (!forward)
        {
            candidates.Reverse();
        }

        if (start is not null)
        {
            var from = new DynamoIndexEntry(start.Order, start.Table);
            candidates = candidates
                .Where(c =>
                {
                    var order = DynamoIndexEntry.Comparer.Compare(new DynamoIndexEntry(c.OrderKey, c.TableKey), from);
                    return forward ? order > 0 : order < 0;
                })
                .ToList();
        }

        return candidates;
    }

    private sealed record ReadResult(List<Dictionary<string, DynamoValue>>? Items, int Count, int ScannedCount, Dictionary<string, DynamoValue>? LastEvaluatedKey, ConsumedCapacity? ConsumedCapacity);

    /// <summary>
    /// Reads candidates as DynamoDB does: up to <c>Limit</c> items (before the filter) or 1 MB,
    /// whichever comes first; then filters and projects what it read.
    /// </summary>
    private static ReadResult Read(ReadPlan plan, IEnumerable<Candidate> candidates)
    {
        var items = new List<Dictionary<string, DynamoValue>>();
        var scanned = 0;
        var bytes = 0L;
        var fetchedUnits = 0.0;
        var fetches = FetchesFromTable(plan);
        Candidate? last = null;
        var stopped = false;
        foreach (var candidate in candidates)
        {
            if (plan.Limit is { } limit && scanned >= limit)
            {
                stopped = true;
                break;
            }

            if (bytes >= MaxPageBytes)
            {
                stopped = true;
                break;
            }

            var source = plan.Index is null || !plan.Index.IsGlobal || plan.Index.Projection.Type == "ALL"
                ? candidate.Item
                : ProjectForIndex(plan.Table, plan.Index, candidate.Item);
            var indexView = plan.Index is null ? source : ProjectForIndex(plan.Table, plan.Index, candidate.Item);
            scanned++;
            bytes += DynamoValueRules.ItemSize(indexView);
            if (fetches)
            {
                fetchedUnits += ReadUnits(DynamoValueRules.ItemSize(candidate.Item), plan.Consistent);
            }

            last = candidate;
            if (plan.Filter is not null && !plan.Filter.Evaluate(source))
            {
                continue;
            }

            if (plan.Select == "COUNT")
            {
                items.Add(source);
                continue;
            }

            items.Add(plan.Projection is not null ? ItemProjection.Apply(source, plan.Projection)
                : plan.AttributesToGet is not null ? ItemProjection.Attributes(source, plan.AttributesToGet)
                : plan.Select == "ALL_PROJECTED_ATTRIBUTES" ? indexView
                : source);
        }

        if (!stopped && plan.Limit is { } exact && scanned == exact && last is not null)
        {
            // DynamoDB stops at the limit without looking ahead, so it answers with the key it stopped at.
            stopped = true;
        }

        Dictionary<string, DynamoValue>? lastEvaluatedKey = null;
        if (stopped && last is { } at)
        {
            lastEvaluatedKey = plan.Table.KeySchema.ToAttributes(at.TableKey);
            if (plan.Index is not null)
            {
                foreach (var entry in plan.Index.KeySchema.ToAttributes(at.OrderKey))
                {
                    lastEvaluatedKey[entry.Key] = entry.Value;
                }
            }
        }

        var units = ReadUnits(bytes, plan.Consistent);
        ConsumedCapacity? consumed;
        if (plan.Index is null)
        {
            consumed = Consumed(plan.ReturnConsumedCapacity, plan.Table, units, write: false);
        }
        else
        {
            // The read went to the index; the table is charged only for the items a local index
            // fetched from it.
            consumed = Consumed(plan.ReturnConsumedCapacity, plan.Table, fetchedUnits, write: false, new Dictionary<string, double>(StringComparer.Ordinal) { [plan.Index.Name] = units });
        }

        return new ReadResult(plan.Select == "COUNT" ? null : items, items.Count, scanned, lastEvaluatedKey, consumed);
    }
}

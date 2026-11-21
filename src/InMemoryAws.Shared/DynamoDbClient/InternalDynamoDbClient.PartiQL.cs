using System.Security.Cryptography;
using InMemoryAws.DynamoDb.Expressions;
using InMemoryAws.DynamoDb.Model;
using InMemoryAws.DynamoDb.PartiQL;

namespace InMemoryAws.DynamoDb;

/// <summary>PartiQL: ExecuteStatement, BatchExecuteStatement and ExecuteTransaction.</summary>
internal sealed partial class InternalDynamoDbClient
{
    private const int MaxBatchStatements = 25;
    private const int MaxTransactionStatements = 100;

    public Task<ExecuteStatementResponse> ExecuteStatementAsync(ExecuteStatementRequest request, CancellationToken cancellationToken = default)
    {
        CheckReturnValuesOnConditionCheckFailure(request.ReturnValuesOnConditionCheckFailure);
        var statement = PartiQLParser.ParseDeferringSourceChecks(request.Statement!, request.Parameters);
        if (statement is PartiQLExists)
        {
            // EXISTS is a transaction's condition check; on its own it checks nothing.
            throw DynamoDbErrors.Validation("Statement wasn't well formed, can't be processed: Expected data manipulation");
        }

        lock (Store.Gate)
        {
            if (statement is PartiQLSelect { SourceRefusal: { } refusal } deferred
                && !(deferred.Index is not null && FindTable(deferred.Table)?.FindIndex(deferred.Index) is { KeySchema.IsMultiAttribute: true }))
            {
                throw refusal;
            }

            var table = ActiveTable(statement.Table);
            Record("PartiQL" + StatementVerb(statement), table.Arn);
            if (statement is PartiQLSelect select)
            {
                return Task.FromResult(Select(table, select, request));
            }

            var write = PlanWrite(table, statement, request.ReturnValuesOnConditionCheckFailure == "ALL_OLD");
            if (write.Error is not null)
            {
                throw write.Error;
            }

            ApplyWrite(write);
            return Task.FromResult(new ExecuteStatementResponse
            {
                Items = write.Returned is null ? [] : [write.Returned],
                ConsumedCapacity = Consumed(request.ReturnConsumedCapacity, table, WriteUnits(table, write.Before, write.After), write: true, IndexWriteUnits(table, write.Before, write.After), vectorBytes: VectorWriteBytes(table, write.Before, write.After)),
            });
        }
    }

    /// <summary>PartiQL's refusal of any read of an index keyed on several attributes in a role.</summary>
    private const string MultiAttributeKeyRefusal = "PartiQL statements cannot be executed on tables with multi-attribute HASH keys or multi-attribute RANGE keys.";

    private static string StatementVerb(PartiQLStatement statement) => statement switch
    {
        PartiQLSelect => "Select",
        PartiQLInsert => "Insert",
        PartiQLUpdate => "Update",
        _ => "Delete",
    };

    private static ExecuteStatementResponse Select(DynamoTable table, PartiQLSelect select, ExecuteStatementRequest request)
    {
        DynamoIndex? index = null;
        if (select.Index is not null)
        {
            // A vector index is read only by SearchVectors.
            if (table.FindVectorIndex(select.Index) is not null)
            {
                throw DynamoDbErrors.Validation("Scan operation not supported on this index type");
            }

            index = table.FindIndex(select.Index) ?? throw DynamoDbErrors.Validation("The table does not have the specified index");
            if (index.KeySchema.IsMultiAttribute)
            {
                throw DynamoDbErrors.Validation(MultiAttributeKeyRefusal);
            }

            if (index.IsGlobal && request.ConsistentRead == true)
            {
                throw DynamoDbErrors.Validation("Strongly consistent read is not supported on Global Secondary Indexes");
            }

            // A filter on an attribute the index does not project is refused only for a read with
            // a key condition on the index; a scan of the index reads that attribute as absent.
            if (index.Projection.Type != "ALL" && select.Where is not null && KeysPartition(select.Where, index.KeySchema.Partition.Name))
            {
                var missing = FilterAttributes(select.Where).Where(a => !IndexProjects(table, index, a)).Distinct(StringComparer.Ordinal).ToList();
                if (missing.Count > 0)
                {
                    throw DynamoDbErrors.InvalidParameter($"Secondary index {index.Name} does not project one or more filter attributes: [{string.Join(", ", missing)}]");
                }
            }

            // A local index fetches what it does not project from its table; a global one has no table to fetch from.
            if (index.IsGlobal && index.Projection.Type != "ALL" && select.Projection is not null)
            {
                var missing = select.Projection.Select(p => p.Path.AttributeName).Where(a => !IndexProjects(table, index, a)).Distinct(StringComparer.Ordinal).ToList();
                if (missing.Count > 0)
                {
                    throw DynamoDbErrors.InvalidParameter($"Global secondary index {index.Name} does not project [{string.Join(", ", missing)}]");
                }
            }
        }

        CheckPinnedKeyTypes(table, select.Equalities);
        var pinned = select.Equalities.GroupBy(e => e.Attribute, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);
        DynamoValue? partition = index is null && pinned.TryGetValue(table.KeySchema.Partition.Name, out var p) ? p : null;
        DynamoValue? sort = partition is not null && table.KeySchema.Sort is { } sortKey && pinned.TryGetValue(sortKey.Name, out var s) ? s : null;

        if (select.OrderBy is { } orderBy)
        {
            var orderable = (index?.KeySchema ?? table.KeySchema).Attributes.Select(a => a.Name);
            if (!orderable.Contains(orderBy.Attribute, StringComparer.Ordinal))
            {
                throw DynamoDbErrors.Validation("ORDER BY is supported only on a key attribute of the source");
            }

            // The source's partition key must be keyed by a top-level condition, on a table or an
            // index alike; an IN on it keys the partition as well as an equality does.
            if (select.Where is null)
            {
                throw DynamoDbErrors.Validation("Must have WHERE clause in the statement when using ORDER BY clause.");
            }

            if (partition is null && !KeysPartition(select.Where, (index?.KeySchema ?? table.KeySchema).Partition.Name))
            {
                throw DynamoDbErrors.Validation("Must have at least one non-optional hash key condition in WHERE clause when using ORDER BY clause.");
            }
        }

        List<(DynamoKey Key, Dictionary<string, DynamoValue> Item)> candidates;
        if (partition is not null)
        {
            candidates = table.Keys.GetViewBetween(DynamoKey.PartitionStart(partition), DynamoKey.PartitionEnd(partition))
                .Where(k => sort is null || (k.Sort is not null && DynamoKeyOrder.Compare(k.Sort, sort) == 0))
                .Select(k => (k, table.Get(k)!))
                .ToList();
        }
        else if (index is null && select.Where is not null && PartitionsIn(select.Where, table.KeySchema.Partition.Name) is { } partitions)
        {
            // An IN on the partition key reads only the partitions it names, in key order.
            candidates = partitions
                .Where(v => v.Type == table.KeySchema.Partition.Type)
                .Distinct()
                .SelectMany(v => table.Keys.GetViewBetween(DynamoKey.PartitionStart(v), DynamoKey.PartitionEnd(v)))
                .OrderBy(k => k, DynamoKey.Comparer)
                .Select(k => (k, table.Get(k)!))
                .ToList();
        }
        else if (index is not null)
        {
            candidates = index.Entries.Select(e => (e.TableKey, table.Get(e.TableKey)!)).ToList();
            if (pinned.TryGetValue(index.KeySchema.Partition.Name, out var indexPartition))
            {
                candidates = candidates
                    .Where(c => c.Item.TryGetValue(index.KeySchema.Partition.Name, out var held) && held.Type == indexPartition.Type && DynamoKeyOrder.Compare(held, indexPartition) == 0)
                    .ToList();
            }
        }
        else
        {
            candidates = table.All().ToList();
        }

        if (select.OrderBy is { } order)
        {
            // The order is total (ties fall back to key order), so the unstable sort is deterministic.
            candidates.Sort((a, b) => ComparePositions(PositionOf(table, index, order, a.Key, a.Item), PositionOf(table, index, order, b.Key, b.Item), order));
        }

        if (request.NextToken is { } token)
        {
            // Resume after the token's position, whether or not its item is still there (or still
            // has the keys it had), as DynamoDB resumes from an exclusive start key.
            var resume = PositionOf(table, index, select.OrderBy, null, DecodeCursor(request.Statement!, token));
            if (resume.TableKey is null || (index is not null && resume.IndexKey is null))
            {
                throw DynamoDbErrors.Validation("Invalid NextToken");
            }

            candidates = candidates.Where(c => ComparePositions(PositionOf(table, index, select.OrderBy, c.Key, c.Item), resume, select.OrderBy) > 0).ToList();
        }

        var limit = request.Limit ?? int.MaxValue;
        var consistent = request.ConsistentRead ?? false;
        var items = new List<Dictionary<string, DynamoValue>>();
        var bytes = 0L;
        var fetchUnits = 0.0;
        var evaluated = 0;

        // Naming an attribute a local index does not project fetches each row's item from the table.
        var fetches = index is { IsGlobal: false } && index.Projection.Type != "ALL" && select.Projection is not null
            && select.Projection.Any(p => !IndexProjects(table, index, p.Path.AttributeName));
        foreach (var (_, item) in candidates)
        {
            if (evaluated >= limit || (evaluated > 0 && bytes >= MaxPageBytes))
            {
                break;
            }

            var held = index is null || index.Projection.Type == "ALL" ? item : ProjectForIndex(table, index, item);
            bytes += table.BilledSize(held);
            if (fetches)
            {
                fetchUnits += ReadUnits(table.BilledSize(item), consistent);
            }

            evaluated++;
            if (select.Where is not null && !select.Where.Evaluate(held))
            {
                continue;
            }

            items.Add(select.Projection is null ? held : ProjectStatement(fetches ? item : held, select.Projection));
        }

        string? nextToken = null;
        Dictionary<string, DynamoValue>? lastEvaluatedKey = null;
        if ((candidates.Count > evaluated || (request.Limit is not null && evaluated >= limit)) && evaluated > 0)
        {
            var (key, item) = candidates[evaluated - 1];
            lastEvaluatedKey = table.KeySchema.ToAttributes(key);
            if (index is not null)
            {
                foreach (var attribute in index.KeySchema.Attributes)
                {
                    if (item.TryGetValue(attribute.Name, out var value))
                    {
                        lastEvaluatedKey[attribute.Name] = value;
                    }
                }
            }

            // The token holds the index's key too, so a page resumes in index order.
            nextToken = EncodeCursor(request.Statement!, lastEvaluatedKey);
        }

        return new ExecuteStatementResponse
        {
            Items = items,
            NextToken = nextToken,
            LastEvaluatedKey = lastEvaluatedKey,
            ConsumedCapacity = index is null
                ? Consumed(request.ReturnConsumedCapacity, table, ReadUnits(bytes, consistent), write: false)
                : Consumed(request.ReturnConsumedCapacity, table, fetchUnits, write: false, new Dictionary<string, double>(StringComparer.Ordinal) { [index.Name] = ReadUnits(bytes, consistent) }),
        };
    }

    /// <summary>Whether a WHERE clause's top-level AND branches pin <paramref name="partition"/> with <c>=</c> or <c>IN</c>.</summary>
    private static bool KeysPartition(ExpressionCondition where, string partition) => where switch
    {
        AndCondition and => and.Conditions.Any(c => KeysPartition(c, partition)),
        CompareCondition { Operator: CompareOperator.Equal } compare =>
            (compare.Left is PathOperand { Path.IsTopLevel: true } l && l.Path.AttributeName == partition && compare.Right is ValueOperand)
            || (compare.Right is PathOperand { Path.IsTopLevel: true } r && r.Path.AttributeName == partition && compare.Left is ValueOperand),
        InCondition { Value: PathOperand { Path.IsTopLevel: true } p } => p.Path.AttributeName == partition,
        _ => false,
    };

    /// <summary>The values a top-level AND branch of a WHERE clause matches <paramref name="partition"/> against with <c>IN</c>, if any.</summary>
    private static IReadOnlyList<DynamoValue>? PartitionsIn(ExpressionCondition where, string partition)
    {
        switch (where)
        {
            case AndCondition and:
                foreach (var condition in and.Conditions)
                {
                    if (PartitionsIn(condition, partition) is { } values)
                    {
                        return values;
                    }
                }

                return null;
            case InCondition { Value: PathOperand { Path.IsTopLevel: true } p } inCondition
                when p.Path.AttributeName == partition && inCondition.Candidates.All(c => c is ValueOperand):
                return inCondition.Candidates.Select(c => ((ValueOperand)c).Value).ToList();
            default:
                return null;
        }
    }

    private static Dictionary<string, DynamoValue> ProjectStatement(Dictionary<string, DynamoValue> item, IReadOnlyList<PartiQLProjection> projection)
    {
        var result = new Dictionary<string, DynamoValue>(StringComparer.Ordinal);
        foreach (var projected in projection)
        {
            if (projected.Path.Get(item) is { } value)
            {
                result[projected.Label] = value;
            }
        }

        return result;
    }

    private static string StatementFingerprint(string statement)
    {
#if NET
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(statement));
#else
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(statement));
#endif
        return Convert.ToBase64String(hash, 0, 12);
    }

    /// <summary>
    /// A row's place in the order SELECT reads rows: by the ORDER BY attribute when there is one,
    /// then in its source's key order (an index's key, then the table's key, for an index).
    /// </summary>
    private sealed record SelectPosition(DynamoKey? TableKey, DynamoKey? IndexKey, DynamoValue? OrderValue);

    private static SelectPosition PositionOf(
        DynamoTable table, DynamoIndex? index, (string Attribute, bool Descending)? order, DynamoKey? tableKey, Dictionary<string, DynamoValue> attributes) =>
        new(
            tableKey ?? table.KeySchema.KeyOf(attributes),
            index?.KeySchema.KeyOf(attributes),
            order is { } o && attributes.TryGetValue(o.Attribute, out var value) ? value : null);

    private static int ComparePositions(SelectPosition a, SelectPosition b, (string Attribute, bool Descending)? order)
    {
        if (order is { } o)
        {
            var (x, y) = (a.OrderValue, b.OrderValue);
            var compared = x is null || y is null ? (x is null ? 0 : 1) - (y is null ? 0 : 1) : DynamoKeyOrder.Compare(x, y);
            if (compared != 0)
            {
                return o.Descending ? -compared : compared;
            }
        }

        if (a.IndexKey is not null && b.IndexKey is not null)
        {
            var indexOrder = DynamoKey.Comparer.Compare(a.IndexKey, b.IndexKey);
            if (indexOrder != 0)
            {
                return indexOrder;
            }
        }

        return DynamoKey.Comparer.Compare(a.TableKey!, b.TableKey!);
    }

    private static string EncodeCursor(string statement, Dictionary<string, DynamoValue> position)
    {
        var json = DynamoValueJson.ToJson(DynamoValue.FromMap(position));
        return StatementFingerprint(statement) + ":" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    private static Dictionary<string, DynamoValue> DecodeCursor(string statement, string token)
    {
        var colon = token.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0 && !string.Equals(token[..colon], StatementFingerprint(statement), StringComparison.Ordinal))
        {
            // A token another statement issued.
            throw DynamoDbErrors.Validation("NextToken does not match request");
        }

        try
        {
            if (colon > 0)
            {
                using var document = System.Text.Json.JsonDocument.Parse(Convert.FromBase64String(token[(colon + 1)..]));
                if (DynamoValueJson.Read(document.RootElement) is { Type: DynamoType.M } position)
                {
                    return position.M.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
                }
            }
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException or DynamoDbException)
        {
        }

        throw DynamoDbErrors.Validation("Invalid NextToken");
    }

    private static void CheckPinnedKeyTypes(DynamoTable table, IReadOnlyList<PartiQLEquality> equalities)
    {
        foreach (var equality in equalities)
        {
            if (table.DeclaredType(equality.Attribute) is { } declared && declared != equality.Value.Type)
            {
                throw DynamoDbErrors.Validation($"Key attribute's data type should match its data type in table's schema: Key {equality.Attribute}");
            }
        }
    }

    /// <summary>The one item a statement's WHERE clause names: an equality on every key attribute.</summary>
    private static DynamoKey SingletonKey(DynamoTable table, IReadOnlyList<PartiQLEquality> equalities)
    {
        CheckPinnedKeyTypes(table, equalities);
        var key = new Dictionary<string, DynamoValue>(StringComparer.Ordinal);
        foreach (var attribute in table.KeySchema.Attributes)
        {
            var pinned = equalities.FirstOrDefault(e => string.Equals(e.Attribute, attribute.Name, StringComparison.Ordinal))
                ?? throw DynamoDbErrors.Validation(PartiQLParser.KeyEqualityMessage);
            key[attribute.Name] = pinned.Value;
        }

        return KeyFromKeyMap(table, key);
    }

    /// <summary>A PartiQL write, worked out but not yet applied, or the error it earns.</summary>
    private sealed class PlannedWrite
    {
        public required DynamoTable Table { get; init; }

        public DynamoKey? Key { get; init; }

        public Dictionary<string, DynamoValue>? Before { get; init; }

        public Dictionary<string, DynamoValue>? After { get; init; }

        public bool Delete { get; init; }

        /// <summary>An <c>EXISTS</c> check, which reads the item and writes nothing.</summary>
        public bool Check { get; init; }

        public Dictionary<string, DynamoValue>? Returned { get; init; }

        public DynamoDbException? Error { get; init; }

        /// <summary>The transaction cancellation code the error stands for.</summary>
        public string? ReasonCode { get; init; }
    }

    private static PlannedWrite PlanWrite(DynamoTable table, PartiQLStatement statement, bool returnOldOnFailure)
    {
        switch (statement)
        {
            case PartiQLInsert insert:
            {
                var item = WithVectorsAsWritten(table, CanonicalItem(insert.Item), insert.Item);
                CheckIndexKeys(table, item, update: false);
                CheckLocalIndexEntrySizes(table, item);
                var key = KeyFromItem(table, item);
                if (table.Get(key) is { } existing)
                {
                    return new PlannedWrite
                    {
                        Table = table,
                        Key = key,
                        Before = existing,
                        Error = new DynamoDbException("DuplicateItemException", "Duplicate primary key exists in table"),
                        ReasonCode = "DuplicateItem",
                    };
                }

                return new PlannedWrite { Table = table, Key = key, After = item };
            }

            case PartiQLUpdate update:
            {
                var key = SingletonKey(table, update.Equalities);
                var before = table.Get(key);
                if (before is null || (update.Where is not null && !update.Where.Evaluate(before)))
                {
                    return ConditionFailed(table, key, before, returnOldOnFailure);
                }

                var after = new Dictionary<string, DynamoValue>(before, StringComparer.Ordinal);
                var changed = update.Update.Apply(after);
                foreach (var path in changed)
                {
                    if (table.KeySchema.IsKeyAttribute(path.AttributeName))
                    {
                        throw DynamoDbErrors.InvalidParameter($"Cannot update attribute {path.AttributeName}. This attribute is part of the key");
                    }
                }

                DynamoValueRules.CheckItemLimits(after, DynamoValueRules.UpdateItemSizeExceeded);
                CheckLocalIndexEntrySizes(table, after, DynamoValueRules.UpdateItemSizeExceeded);
                CheckIndexKeys(table, after, update: true);
                var returned = update.Returning switch
                {
                    "ALL_OLD" => before,
                    "ALL_NEW" => after,
                    "UPDATED_OLD" => NonEmpty(ItemProjection.Apply(before, changed)),
                    "UPDATED_NEW" => NonEmpty(ItemProjection.Apply(after, changed)),
                    _ => null,
                };
                return new PlannedWrite { Table = table, Key = key, Before = before, After = after, Returned = returned };
            }

            case PartiQLDelete delete:
            {
                var key = SingletonKey(table, delete.Equalities);
                var before = table.Get(key);
                if (before is null)
                {
                    // Deleting what is not there is done already: no item, so no predicate to fail.
                    return new PlannedWrite { Table = table, Key = key };
                }

                if (delete.Where is not null && !delete.Where.Evaluate(before))
                {
                    return ConditionFailed(table, key, before, returnOldOnFailure);
                }

                return new PlannedWrite { Table = table, Key = key, Before = before, Delete = true, Returned = delete.Returning is null ? null : before };
            }

            case PartiQLExists exists:
            {
                var key = SingletonKey(table, exists.Equalities);
                var before = table.Get(key);
                if (before is null || (exists.Where is not null && !exists.Where.Evaluate(before)))
                {
                    return ConditionFailed(table, key, before, returnOldOnFailure);
                }

                return new PlannedWrite { Table = table, Key = key, Before = before, Check = true };
            }

            default:
                throw new InvalidOperationException("Not a write statement.");
        }
    }

    private static PlannedWrite ConditionFailed(DynamoTable table, DynamoKey key, Dictionary<string, DynamoValue>? before, bool returnOld) => new()
    {
        Table = table,
        Key = key,
        Before = before,
        Error = new DynamoDbConditionalCheckFailedException(returnOld ? before : null),
        ReasonCode = "ConditionalCheckFailed",
    };

    private void ApplyWrite(PlannedWrite write)
    {
        if (write.Delete)
        {
            Changed(write.Table, write.Table.Delete(write.Key!));
        }
        else if (write.After is not null)
        {
            Changed(write.Table, write.Table.Put(write.Key!, write.After));
        }
    }

    private const string BatchKeyEqualityMessage = "Select statements within BatchExecuteStatement must specify the primary key in the where clause.";

    /// <summary>
    /// Whether a WHERE clause ORs a branch naming one item (partition and sort key) with a branch
    /// naming only the partition: the second covers the first's sort key range.
    /// </summary>
    private static bool OverlapsSortKey(DynamoTable table, ExpressionCondition? where)
    {
        if (where is not OrCondition or || table.KeySchema.Sort is not { } sort)
        {
            return false;
        }

        var branches = or.Conditions.Select(PinnedAttributes).ToList();
        return branches.TrueForAll(b => b.Contains(table.KeySchema.Partition.Name))
            && branches.Exists(b => b.Contains(sort.Name))
            && branches.Exists(b => !b.Contains(sort.Name));
    }

    /// <summary>The top-level attributes a condition's AND branches compare with a value by <c>=</c>.</summary>
    private static HashSet<string> PinnedAttributes(ExpressionCondition condition)
    {
        var pinned = new HashSet<string>(StringComparer.Ordinal);
        switch (condition)
        {
            case AndCondition and:
                foreach (var branch in and.Conditions)
                {
                    pinned.UnionWith(PinnedAttributes(branch));
                }

                break;
            case CompareCondition { Operator: CompareOperator.Equal } compare:
                if (compare.Left is PathOperand { Path.IsTopLevel: true } l && compare.Right is ValueOperand)
                {
                    pinned.Add(l.Path.AttributeName);
                }
                else if (compare.Right is PathOperand { Path.IsTopLevel: true } r && compare.Left is ValueOperand)
                {
                    pinned.Add(r.Path.AttributeName);
                }

                break;
        }

        return pinned;
    }

    public Task<BatchExecuteStatementResponse> BatchExecuteStatementAsync(BatchExecuteStatementRequest request, CancellationToken cancellationToken = default)
    {
        var statements = request.Statements!;
        if (statements.Count > MaxBatchStatements)
        {
            throw DynamoDbErrors.ValidationClause(AwsValidation.Clause(JavaRendering.List(statements, s => s.ToString()), "statements", $"Member must have length less than or equal to {MaxBatchStatements}"));
        }

        var parsed = new List<(BatchStatementRequest Request, PartiQLStatement? Statement, DynamoDbException? Error)>();
        foreach (var statement in statements)
        {
            CheckReturnValuesOnConditionCheckFailure(statement.ReturnValuesOnConditionCheckFailure);
            try
            {
                parsed.Add((statement, PartiQLParser.Parse(statement.Statement!, statement.Parameters), null));
            }
            catch (DynamoDbException ex) when (ex.ErrorCode == "ValidationException")
            {
                parsed.Add((statement, null, ex));
            }
        }

        var kinds = parsed.Where(p => p.Statement is not null).Select(p => p.Statement!.IsRead).Distinct().Count();
        if (kinds > 1)
        {
            throw DynamoDbErrors.Validation("The entire batch must consist of either read statements or write statements, you cannot mix both in one batch.");
        }

        lock (Store.Gate)
        {
            var responses = new List<BatchStatementResponse>();
            var units = new Dictionary<DynamoTable, double>();
            var indexUnits = new Dictionary<DynamoTable, Dictionary<string, double>>();
            var vectorBytes = new Dictionary<DynamoTable, Dictionary<string, double>>();
            foreach (var (statementRequest, statement, parseError) in parsed)
            {
                if (statement is null)
                {
                    responses.Add(new BatchStatementResponse { Error = new BatchStatementError { Code = "ValidationError", Message = parseError!.Message } });
                    continue;
                }

                if (FindTable(statement.Table) is not { Status: not "CREATING" } table)
                {
                    responses.Add(new BatchStatementResponse { Error = new BatchStatementError { Code = "ResourceNotFound", Message = DynamoDbErrors.RequestedResourceNotFound } });
                    continue;
                }

                try
                {
                    if (statement is PartiQLSelect select)
                    {
                        // A read that names no single item of the table is refused before it runs,
                        // so its slot names no table.
                        DynamoKey? key = null;
                        string? refusal = null;
                        if (select.Index is not null)
                        {
                            refusal = table.FindIndex(select.Index) is { KeySchema.IsMultiAttribute: true }
                                ? MultiAttributeKeyRefusal
                                : BatchKeyEqualityMessage;
                        }
                        else if (select.OrderBy is not null)
                        {
                            refusal = "ORDER BY is not supported for a read statement in a BatchExecuteStatement, which reports at most one item";
                        }
                        else
                        {
                            try
                            {
                                key = SingletonKey(table, select.Equalities);
                            }
                            catch (DynamoDbException ex) when (ex.Message == PartiQLParser.KeyEqualityMessage)
                            {
                                refusal = OverlapsSortKey(table, select.Where)
                                    ? "Overlapping conditions with range keys are not supported in where clause"
                                    : BatchKeyEqualityMessage;
                            }
                        }

                        if (key is null)
                        {
                            responses.Add(new BatchStatementResponse { Error = new BatchStatementError { Code = "ValidationError", Message = refusal } });
                            continue;
                        }

                        var item = table.Get(key);
                        units[table] = (units.TryGetValue(table, out var read) ? read : 0) + ReadUnits(table.BilledSize(item), statementRequest.ConsistentRead ?? false);
                        var matched = item is not null && (select.Where is null || select.Where.Evaluate(item));
                        responses.Add(new BatchStatementResponse
                        {
                            TableName = table.Name,
                            Item = matched ? (select.Projection is null ? item : ProjectStatement(item!, select.Projection)) : null,
                        });
                    }
                    else if (statement is PartiQLExists)
                    {
                        throw DynamoDbErrors.Validation("Statement wasn't well formed, can't be processed: Expected data manipulation");
                    }
                    else
                    {
                        var write = PlanWrite(table, statement, statementRequest.ReturnValuesOnConditionCheckFailure == "ALL_OLD");
                        if (write.Error is not null)
                        {
                            responses.Add(new BatchStatementResponse
                            {
                                TableName = table.Name,
                                Error = new BatchStatementError
                                {
                                    Code = write.ReasonCode,
                                    Message = write.Error.Message,
                                    Item = (write.Error as DynamoDbConditionalCheckFailedException)?.Item,
                                },
                            });
                            continue;
                        }

                        ApplyWrite(write);
                        units[table] = (units.TryGetValue(table, out var written) ? written : 0) + WriteUnits(table, write.Before, write.After);
                        AddIndexWriteUnits(indexUnits, table, write.Before, write.After);
                        AddVectorWriteBytes(vectorBytes, table, write.Before, write.After);
                        responses.Add(new BatchStatementResponse { TableName = table.Name, Item = write.Returned });
                    }

                    Record("BatchExecuteStatement", table.Arn);
                }
                catch (DynamoDbException ex) when (ex.ErrorCode == "ValidationException")
                {
                    responses.Add(new BatchStatementResponse { TableName = table.Name, Error = new BatchStatementError { Code = "ValidationError", Message = ex.Message } });
                }
            }

            var capacity = units.Select(u => Consumed(request.ReturnConsumedCapacity, u.Key, u.Value, write: kinds == 1 && !parsed.First(p => p.Statement is not null).Statement!.IsRead, indexUnits.TryGetValue(u.Key, out var indexes) ? indexes : null, vectorBytes: vectorBytes.TryGetValue(u.Key, out var vectors) ? vectors : null)).Where(c => c is not null).Select(c => c!).ToList();
            return Task.FromResult(new BatchExecuteStatementResponse
            {
                Responses = responses,
                ConsumedCapacity = capacity.Count > 0 ? capacity : null,
            });
        }
    }

    public Task<ExecuteTransactionResponse> ExecuteTransactionAsync(ExecuteTransactionRequest request, CancellationToken cancellationToken = default)
    {
        var transactStatements = request.TransactStatements!;
        if (transactStatements.Count > MaxTransactionStatements)
        {
            throw DynamoDbErrors.ValidationClause(AwsValidation.Clause(JavaRendering.List(transactStatements, s => s.ToString()), "transactStatements", $"Member must have length less than or equal to {MaxTransactionStatements}"));
        }

        var statements = new List<(ParameterizedStatement Request, PartiQLStatement Statement)>();
        for (var slot = 0; slot < transactStatements.Count; slot++)
        {
            var statement = transactStatements[slot];
            CheckReturnValuesOnConditionCheckFailure(statement.ReturnValuesOnConditionCheckFailure);
            PartiQLStatement parsed;
            try
            {
                parsed = PartiQLParser.Parse(statement.Statement!, statement.Parameters);
            }
            catch (DynamoDbException ex) when (ex.ErrorCode == "ValidationException")
            {
                // A statement is refused as the slot it was sent in, so the caller can tell which is wrong.
                throw InSlot(slot, ex.Message);
            }

            // No transaction reads an index: a refusal of the request, not a cancellation of it.
            if (parsed is PartiQLSelect { Index: not null })
            {
                throw InSlot(slot, "Reads on indices are not supported within transactions.");
            }

            statements.Add((statement, parsed));
        }

        if (statements.Select(s => s.Statement.IsRead).Distinct().Count() > 1)
        {
            throw DynamoDbErrors.Validation("ExecuteTransaction API does not support both read and write operations in the same request.");
        }

        var reads = statements.Count > 0 && statements[0].Statement.IsRead;
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

                fingerprint = "partiql|" + string.Join("|", transactStatements.Select(s => s.ToString()));
                if (Store.TransactionTokens.TryGetValue(token, out var remembered))
                {
                    if (!string.Equals(remembered.RequestFingerprint, fingerprint, StringComparison.Ordinal))
                    {
                        throw new DynamoDbException("IdempotentParameterMismatchException", "Specified idempotent token was used with different request parameters within the idempotency window");
                    }

                    // The same transaction again: it applies nothing, and is billed as reading what the first call wrote.
                    var replayed = (remembered.Ledger ?? new Dictionary<string, double>())
                        .Where(u => Store.Tables.ContainsKey(u.Key))
                        .Select(u => Consumed(request.ReturnConsumedCapacity, Store.Tables[u.Key], u.Value, write: false, transactional: true))
                        .Where(c => c is not null)
                        .Select(c => c!)
                        .ToList();
                    return Task.FromResult(new ExecuteTransactionResponse { Responses = [], ConsumedCapacity = replayed.Count > 0 ? replayed : null });
                }
            }

            // A statement naming a table that is not there cancels the transaction, naming the statement.
            var tables = new List<DynamoTable?>();
            foreach (var (_, statement) in statements)
            {
                try
                {
                    tables.Add(ActiveTable(statement.Table));
                }
                catch (DynamoDbException ex) when (ex.ErrorCode == "ResourceNotFoundException")
                {
                    tables.Add(null);
                }
            }

            if (tables.Contains(null))
            {
                throw new DynamoDbTransactionCanceledException(tables
                    .Select(t => t is null ? new CancellationReason("ResourceNotFound", DynamoDbErrors.RequestedResourceNotFound) : CancellationReason.None)
                    .ToList());
            }

            if (reads)
            {
                var responses = new List<ItemResponse>();
                var readUnits = new Dictionary<DynamoTable, double>();
                var seen = new List<(DynamoTable, DynamoKey)>();
                for (var i = 0; i < statements.Count; i++)
                {
                    var select = (PartiQLSelect)statements[i].Statement;
                    var table = tables[i]!;
                    if (select.OrderBy is not null)
                    {
                        throw DynamoDbErrors.Validation("ORDER BY is not supported for a read statement in a transaction, which reports at most one item");
                    }

                    DynamoKey key;
                    try
                    {
                        key = SingletonKey(table, select.Equalities);
                    }
                    catch (DynamoDbException ex) when (ex.Message == PartiQLParser.KeyEqualityMessage)
                    {
                        throw DynamoDbErrors.Validation("Select statements within ExecuteTransaction must specify the primary key in the where clause.");
                    }

                    if (seen.Any(s => ReferenceEquals(s.Item1, table) && s.Item2.Equals(key)))
                    {
                        throw DynamoDbErrors.Validation("Transaction request cannot include multiple operations on one item.");
                    }

                    seen.Add((table, key));
                    var item = table.Get(key);
                    var matched = item is not null && (select.Where is null || select.Where.Evaluate(item));
                    responses.Add(new ItemResponse { Item = matched ? (select.Projection is null ? item : ProjectStatement(item!, select.Projection)) : null });
                    readUnits[table] = (readUnits.TryGetValue(table, out var running) ? running : 0) + ReadUnits(table.BilledSize(item), consistent: true, transactional: true);
                    Record("ExecuteTransaction", table.Arn);
                }

                var readCapacity = readUnits.Select(u => Consumed(request.ReturnConsumedCapacity, u.Key, u.Value, write: false, transactional: true)).Where(c => c is not null).Select(c => c!).ToList();
                return Task.FromResult(new ExecuteTransactionResponse { Responses = responses, ConsumedCapacity = readCapacity.Count > 0 ? readCapacity : null });
            }

            var writes = new List<PlannedWrite>();
            for (var i = 0; i < statements.Count; i++)
            {
                var write = PlanWrite(tables[i]!, statements[i].Statement, statements[i].Request.ReturnValuesOnConditionCheckFailure == "ALL_OLD");
                if (write.Key is { } key && writes.Any(w => ReferenceEquals(w.Table, write.Table) && w.Key!.Equals(key)))
                {
                    throw DynamoDbErrors.Validation("Transaction request cannot include multiple operations on one item.");
                }

                writes.Add(write);
            }

            if (writes.Any(w => w.Error is not null))
            {
                throw new DynamoDbTransactionCanceledException(writes.Select(w => w.Error is null
                    ? CancellationReason.None
                    : new CancellationReason(w.ReasonCode, w.Error.Message, (w.Error as DynamoDbConditionalCheckFailedException)?.Item)).ToList());
            }

            var units = new Dictionary<DynamoTable, double>();
            var indexUnits = new Dictionary<DynamoTable, Dictionary<string, double>>();
            var vectorBytes = new Dictionary<DynamoTable, Dictionary<string, double>>();
            foreach (var write in writes)
            {
                ApplyWrite(write);
                var cost = write.Check
                    ? ReadUnits(write.Table.BilledSize(write.Before), consistent: true, transactional: true)
                    : WriteUnits(write.Table, write.Before, write.After, transactional: true);
                units[write.Table] = (units.TryGetValue(write.Table, out var running) ? running : 0) + cost;
                if (!write.Check)
                {
                    AddIndexWriteUnits(indexUnits, write.Table, write.Before, write.After);
                    AddVectorWriteBytes(vectorBytes, write.Table, write.Before, write.After);
                }
                Record("ExecuteTransaction", write.Table.Arn);
            }

            if (request.ClientRequestToken is { } requestToken)
            {
                var ledger = units.ToDictionary(u => u.Key.Name, u => u.Value, StringComparer.Ordinal);
                Store.TransactionTokens[requestToken] = new DynamoTransactionToken(fingerprint!, now + TransactionTokenLifetime, ledger);
            }

            var capacity = units.Select(u => Consumed(request.ReturnConsumedCapacity, u.Key, u.Value, write: true, indexUnits.TryGetValue(u.Key, out var indexes) ? indexes : null, transactional: true, vectorBytes.TryGetValue(u.Key, out var vectors) ? vectors : null)).Where(c => c is not null).Select(c => c!).ToList();
            return Task.FromResult(new ExecuteTransactionResponse
            {
                // A write transaction answers only the items its RETURNING clauses asked for.
                Responses = writes.Where(w => w.Returned is not null).Select(w => new ItemResponse { Item = w.Returned }).ToList(),
                ConsumedCapacity = capacity.Count > 0 ? capacity : null,
            });
        }
    }

    private static DynamoDbException InSlot(int slot, string message) =>
        DynamoDbErrors.Validation($"Validation failed in TransactStatements[{slot.ToString(CultureInfo.InvariantCulture)}]: {message}");
}

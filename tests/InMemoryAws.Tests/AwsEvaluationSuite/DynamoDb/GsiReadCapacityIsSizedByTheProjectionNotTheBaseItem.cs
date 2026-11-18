// Translated from behavioral/dynamodb/check_gsi_read_capacity_is_sized_by_the_projection_not_the_base_item.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

// Capacity rule: Query/Scan sums the RAW bytes of every item examined, rounds the TOTAL to the 4 KB
// boundary once (not per item), then halves for the eventually consistent GSI read.
//   H1  A Query served by a KEYS_ONLY GSI bills on the size of the INDEX entries (a few tens of
//       bytes each), not on the base items: exactly KeysOnlyExpected units for this check's data.
//   H2  A Query served by an ALL-projection GSI over the same 40 ~4 KB items bills exactly
//       AllExpected units.
//   H3  ReturnConsumedCapacity=INDEXES on an index-served Query charges the index and reports
//       Table.CapacityUnits == 0.
//   H4  A GSI Query cannot be strongly consistent, so both numbers are eventual-read numbers and the
//       0.5 factor applies to BOTH sides equally.
//   H5  The KEYS_ONLY result items contain exactly {pk, gsi1pk} and nothing else.

/// <summary>dynamodb.gsi-read-capacity-is-sized-by-the-projection-not-the-base-item</summary>
[Category("AwsEvaluationSuite")]
public sealed class GsiReadCapacityIsSizedByTheProjectionNotTheBaseItem
{
    private const string CheckId = "gsi-read-capacity-is-sized-by-the-projection-not-the-base-item";
    private const int Items = 40;
    private const string Partition = "BUCKET#1";
    private const string KeysOnlyIndex = "idx-keys-only";
    private const string AllIndex = "idx-all";
    private static readonly string Payload = new('q', 3800); // each base item ≈4 KB -> one read unit
    private static readonly string[] PkValues = [.. Enumerable.Range(0, Items).Select(i => "ITEM#" + i.ToString("000", CultureInfo.InvariantCulture))];
    private static readonly int PkBytes = PkValues[0].Length;

    // DynamoDB automatically projects the base table's key and the index key into every GSI entry
    // regardless of ProjectionType, so a KEYS_ONLY entry holds exactly {pk, gsi1pk}.
    private static readonly int KeysOnlyItemBytes = ("pk".Length + PkBytes) + ("gsi1pk".Length + Partition.Length);
    private static readonly int AllItemBytes = KeysOnlyItemBytes + ("payload".Length + Payload.Length);
    private static readonly double KeysOnlyExpected = Rcu(Items * KeysOnlyItemBytes);
    private static readonly double AllExpected = Rcu(Items * AllItemBytes);
    private static readonly string[] KeysOnlyMembers = ["gsi1pk", "pk"];
    private static readonly string[] AllMembers = ["gsi1pk", "payload", "pk"];

    /// <summary>
    /// DynamoDB Query/Scan capacity: sum the bytes of every item examined, round the TOTAL up to
    /// the 4 KB boundary once (not per item), then halve for an eventually consistent read -- a GSI
    /// Query is always eventually consistent.
    /// </summary>
    private static double Rcu(int totalItemBytes) => Math.Ceiling(totalItemBytes / 4096.0) / 2;

    private static async Task DrainBatch(IAmazonDynamoDB ddb, string table, List<WriteRequest> requests, int attempts = 30)
    {
        var pending = new Dictionary<string, List<WriteRequest>>(StringComparer.Ordinal) { [table] = requests };
        for (var i = 0; i < attempts; i++)
        {
            if (pending.Count == 0)
            {
                return;
            }

            pending = (await ddb.BatchWriteItemAsync(new BatchWriteItemRequest { RequestItems = pending })).UnprocessedItems ?? [];
        }

        throw Fail($"BatchWriteItem never drained UnprocessedItems: {Py.Repr(pending)}");
    }

    /// <summary>A poll's timeout: capSeconds, bounded by the runner's remaining time less 5 s.</summary>
    private static double BudgetSeconds(FidelityContext ctx, double capSeconds) => Math.Max(1.0, Math.Min(capSeconds, ctx.TimeLeftSeconds - 5.0));

    /// <summary>
    /// QueryIndex until the eventually consistent GSI holds <paramref name="want"/> entries or capSeconds ends; a stop
    /// forced by the runner's budget, short of capSeconds, is no verdict.
    /// </summary>
    private static async Task<(List<Dictionary<string, AttributeValue>> Items, double Charged, ConsumedCapacity Consumed)> QueryIndexSettled(
        FidelityContext ctx, IAmazonDynamoDB ddb, string table, string index, int want, double capSeconds = 30.0)
    {
        var clock = FidelityTime.StartNew();
        var budgetSeconds = BudgetSeconds(ctx, capSeconds);
        while (true)
        {
            var result = await QueryIndex(ddb, table, index);
            if (result.Items.Count >= want)
            {
                return result;
            }

            if (clock.Elapsed.TotalSeconds >= budgetSeconds)
            {
                if (budgetSeconds < capSeconds)
                {
                    throw new TimeoutException(
                        $"the check's time budget ran out after {clock.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)}s, before the {Py.Repr(capSeconds)}s wait ended ({result.Items.Count} of {want} entries on {Py.Repr(index)}); not graded");
                }

                return result;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1.0));
        }
    }

    /// <summary>One index Query, hard-failing if it paginates -- a partial page would halve the capacity.</summary>
    private static async Task<(List<Dictionary<string, AttributeValue>> Items, double Charged, ConsumedCapacity Consumed)> QueryIndex(IAmazonDynamoDB ddb, string table, string index)
    {
        var resp = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = index,
            KeyConditionExpression = "gsi1pk = :g",
            ExpressionAttributeValues = Item((":g", S(Partition))),
            ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES,
        });
        Ensure(
            resp.LastEvaluatedKey is null,
            $"this check compares single-page capacity, so the Query on {Py.Repr(index)} must not paginate at this size; it returned LastEvaluatedKey {Py.Repr(resp.LastEvaluatedKey)}");
        var consumed = resp.ConsumedCapacity;
        var charged = consumed.GlobalSecondaryIndexes[index].CapacityUnits!.Value;
        return (resp.Items, charged, consumed);
    }

    /// <summary>
    /// Once both GSIs hold all 40 items, a KEYS_ONLY index Query bills exactly the size of its
    /// index entries and an ALL-projection Query over the same items bills exactly the size of the
    /// full items, both charged to the index and not the base table.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.gsi-read-capacity-is-sized-by-the-projection-not-the-base-item")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        // this check predicts capacity to the exact byte, so every pk value must be the same length
        Ensure(PkValues.Select(v => v.Length).Distinct().Count() == 1, "this check predicts capacity to the exact byte, so every pk value must be the same length");
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-gsisize-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("gsi1pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = KeysOnlyIndex,
                    KeySchema = [new KeySchemaElement("gsi1pk", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.KEYS_ONLY },
                },
                new GlobalSecondaryIndex
                {
                    IndexName = AllIndex,
                    KeySchema = [new KeySchemaElement("gsi1pk", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            Tags = [new Tag { Key = "sim2real:managed", Value = "true" }, new Tag { Key = "sim2real:check", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        var expectedKeys = PkValues.Order(StringComparer.Ordinal).ToList();
        for (var offset = 0; offset < Items; offset += 25)
        {
            await DrainBatch(ddb, table, [.. expectedKeys.Skip(offset).Take(25).Select(pk => new WriteRequest
            {
                PutRequest = new PutRequest { Item = Item(("pk", S(pk)), ("gsi1pk", S(Partition)), ("payload", S(Payload))) },
            })]);
        }

        var (keysOnlyItems, keysOnlyCap, keysOnlyConsumed) = await QueryIndexSettled(ctx, ddb, table, KeysOnlyIndex, Items);
        var (allItems, allCap, allConsumed) = await QueryIndexSettled(ctx, ddb, table, AllIndex, Items);

        // Both indexes must really have answered for all 40 items, or the numbers measure less work.
        Ensure(
            keysOnlyItems.Select(i => i["pk"].S).Order(StringComparer.Ordinal).SequenceEqual(expectedKeys),
            $"the KEYS_ONLY index must return all {Py.Repr(Items)} entries for {Py.Repr(Partition)}; it returned {Py.Repr(keysOnlyItems.Count)}, missing {Py.Repr(expectedKeys.Except(keysOnlyItems.Select(i => i["pk"].S), StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(10).ToList())}");
        Ensure(
            allItems.Select(i => i["pk"].S).Order(StringComparer.Ordinal).SequenceEqual(expectedKeys),
            $"the ALL index must return all {Py.Repr(Items)} entries for {Py.Repr(Partition)}; it returned {Py.Repr(allItems.Count)}");

        // The projection really is a projection.
        var keysOnlyMembers = keysOnlyItems.SelectMany(i => i.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        Ensure(
            keysOnlyMembers.SequenceEqual(KeysOnlyMembers),
            $"a KEYS_ONLY index entry holds the base key and the index key and nothing else, so the result items must have members ['gsi1pk', 'pk'], got {Py.Repr(keysOnlyMembers)}");
        var allMembers = allItems.SelectMany(i => i.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        Ensure(
            allMembers.SequenceEqual(AllMembers),
            $"an ALL-projection index entry holds every base attribute, so the result items must have members ['gsi1pk', 'payload', 'pk'], got {Py.Repr(allMembers)}");

        // The charge lands on the index, not the base table -- the premise of the comparison.
        foreach (var (index, consumed) in new[] { (KeysOnlyIndex, keysOnlyConsumed), (AllIndex, allConsumed) })
        {
            Ensure(
                (consumed.Table?.CapacityUnits ?? -1) == 0,
                $"a Query served BY {Py.Repr(index)} must charge the index and not the base table, got Table {Py.Repr(consumed.Table is null ? null : new Dictionary<string, object?> { ["CapacityUnits"] = consumed.Table.CapacityUnits })}");
        }

        Ensure(
            keysOnlyCap > 0,
            $"the KEYS_ONLY Query read {Py.Repr(Items)} entries and must bill something, got {Py.Repr(keysOnlyCap)} -- with a zero baseline the comparison below is vacuous");
        Ensure(
            keysOnlyCap == KeysOnlyExpected,
            $"{Py.Repr(Items)} KEYS_ONLY index entries totalling {Py.Repr(Items * KeysOnlyItemBytes)} raw bytes must bill exactly {Py.Repr(KeysOnlyExpected)} read units (ceil(bytes/4096)/2 for an eventually consistent GSI read); it billed {Py.Repr(keysOnlyCap)} -- a target that resolves the index by fetching the ~4 KB BASE items would bill far more than this");
        Ensure(
            allCap == AllExpected,
            $"{Py.Repr(Items)} ALL-projection index entries totalling {Py.Repr(Items * AllItemBytes)} raw bytes must bill exactly {Py.Repr(AllExpected)} read units (ceil(bytes/4096)/2 for an eventually consistent GSI read); it billed {Py.Repr(allCap)}");
        Ensure(
            allCap > keysOnlyCap,
            $"two indexes on the same key over the same {Py.Repr(Items)} items must cost differently because they store different bytes: KEYS_ONLY {Py.Repr(keysOnlyCap)} vs ALL {Py.Repr(allCap)}");
    }
}

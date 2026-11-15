// Translated from behavioral/dynamodb/check_partiql_partition_key_predicate_is_planned_as_a_query_not_a_scan.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-partition-key-predicate-is-planned-as-a-query-not-a-scan</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlPartitionKeyPredicateIsPlannedAsAQueryNotAScan
{
    private const string CheckId = "partiql-partition-key-predicate-is-planned-as-a-query-not-a-scan";
    private const int Partitions = 10;
    private const int PerPartition = 30;
    private const string TargetPartition = "P#03";
    private static readonly string Payload = new('y', 900); // ≈1 KB item, so the whole table is ≈300 KB: one page, many RCU

    /// <summary>BatchWriteItem until UnprocessedItems is empty; raises rather than silently under-writing.</summary>
    private static async Task DrainBatch(IAmazonDynamoDB ddb, string table, List<WriteRequest> requests, int attempts = 30)
    {
        var pending = new Dictionary<string, List<WriteRequest>> { [table] = [.. requests] };
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

    /// <summary>The (pk, sk) identity of every returned item, sorted -- the plan-independent answer.</summary>
    private static List<(string, int)> Ids(List<Dictionary<string, AttributeValue>> items) =>
        items.Select(i => (i["pk"].S, int.Parse(i["sk"].N, CultureInfo.InvariantCulture)))
            .OrderBy(t => t.Item1, StringComparer.Ordinal).ThenBy(t => t.Item2).ToList();

    /// <summary>
    /// A PartiQL SELECT pinning the partition key bills like the equivalent Query; on a non-key
    /// attribute it bills like a filtered full Scan, though both return the same items.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-partition-key-predicate-is-planned-as-a-query-not-a-scan")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-pqplan-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.N)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real:managed", Value = "true" }, new Tag { Key = "sim2real:check", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        // `tag` mirrors `pk` in VALUE but is not part of any key, so the two predicates below select the
        // same items through different plans.
        for (var p = 0; p < Partitions; p++)
        {
            var pk = $"P#{p:00}";
            for (var offset = 0; offset < PerPartition; offset += 25)
            {
                await DrainBatch(ddb, table, Enumerable.Range(offset, Math.Min(offset + 25, PerPartition) - offset)
                    .Select(s => new WriteRequest(new PutRequest(Item(
                        ("pk", S(pk)),
                        ("sk", N(s.ToString(CultureInfo.InvariantCulture))),
                        ("tag", S(pk)),
                        ("payload", S(Payload))))))
                    .ToList());
            }
        }

        var expectedIds = Enumerable.Range(0, PerPartition).Select(s => (TargetPartition, s)).ToList();

        // Reference capacities come from the low-level plans on the same data; every read is strongly
        // consistent so no 0.5x eventual-read factor enters one side only.
        var nativeQuery = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :p",
            ExpressionAttributeValues = Item((":p", S(TargetPartition))),
            ConsistentRead = true,
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        });
        var nativeFilteredScan = await ddb.ScanAsync(new ScanRequest
        {
            TableName = table,
            FilterExpression = "tag = :p",
            ExpressionAttributeValues = Item((":p", S(TargetPartition))),
            ConsistentRead = true,
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        });
        var nativeFullScan = await ddb.ScanAsync(new ScanRequest
        {
            TableName = table,
            ConsistentRead = true,
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        });
        foreach (var (label, lek) in new[]
                 {
                     ("Query(pk = :p)", nativeQuery.LastEvaluatedKey),
                     ("Scan(FilterExpression tag = :p)", nativeFilteredScan.LastEvaluatedKey),
                     ("Scan(unfiltered)", nativeFullScan.LastEvaluatedKey),
                 })
        {
            Ensure(
                lek is null,
                $"this check compares single-page capacity, so {label} must not paginate at this table size; it returned LastEvaluatedKey {Py.Repr(lek)}");
        }

        var queryCap = nativeQuery.ConsumedCapacity.CapacityUnits;
        var filteredScanCap = nativeFilteredScan.ConsumedCapacity.CapacityUnits;
        var fullScanCap = nativeFullScan.ConsumedCapacity.CapacityUnits;

        Ensure(
            Ids(nativeQuery.Items).SequenceEqual(expectedIds),
            $"the reference Query must return the {PerPartition} items of {Py.Repr(TargetPartition)}, got {nativeQuery.Items.Count}");
        Ensure(
            Ids(nativeFilteredScan.Items).SequenceEqual(expectedIds),
            $"the reference filtered Scan must return the same {PerPartition} items, got {nativeFilteredScan.Items.Count}");
        Ensure(
            filteredScanCap == fullScanCap,
            $"a FilterExpression is applied after the read, so a filtered Scan must bill the same as the unfiltered Scan of the same table: filtered {Py.Repr(filteredScanCap)} vs unfiltered {Py.Repr(fullScanCap)}");
        // The experiment is only meaningful if the two low-level plans really cost differently here.
        Ensure(
            fullScanCap > queryCap,
            $"this table is not big enough to distinguish the plans on this target: a full Scan billed {Py.Repr(fullScanCap)} and a single-partition Query billed {Py.Repr(queryCap)}. Nothing below can discriminate; grow _PARTITIONS.");

        // --- the same two plans, expressed in PartiQL ---
        var pqKey = await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"SELECT * FROM \"{table}\" WHERE pk = ?",
            Parameters = [S(TargetPartition)],
            ConsistentRead = true,
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        });
        var pqNonkey = await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"SELECT * FROM \"{table}\" WHERE tag = ?",
            Parameters = [S(TargetPartition)],
            ConsistentRead = true,
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        });
        foreach (var (label, token) in new[] { ("WHERE pk = ?", pqKey.NextToken), ("WHERE tag = ?", pqNonkey.NextToken) })
        {
            Ensure(
                token is null,
                $"this check compares single-page capacity, so the PartiQL SELECT {label} must not paginate at this table size; it returned NextToken {Py.Repr(token)}");
        }

        // Both statements must answer the SAME question -- otherwise a cheap number means nothing.
        Ensure(
            Ids(pqKey.Items).SequenceEqual(expectedIds),
            $"SELECT ... WHERE pk = ? must return the {PerPartition} items of {Py.Repr(TargetPartition)}, got {Py.Repr(Ids(pqKey.Items).Take(5).ToList())} ({pqKey.Items.Count} items)");
        Ensure(
            Ids(pqNonkey.Items).SequenceEqual(expectedIds),
            $"SELECT ... WHERE tag = ? selects the same {PerPartition} items by a non-key attribute, got {pqNonkey.Items.Count} items");

        var pqKeyCap = pqKey.ConsumedCapacity.CapacityUnits;
        var pqNonkeyCap = pqNonkey.ConsumedCapacity.CapacityUnits;

        Ensure(
            pqKeyCap == queryCap,
            $"a SELECT whose WHERE clause pins the partition key must be planned as a Query and bill what the equivalent Query billed on this very data: PartiQL {Py.Repr(pqKeyCap)} vs Query {Py.Repr(queryCap)} (a full Scan of this table bills {Py.Repr(fullScanCap)}, so a PartiQL number near that means the statement was scanned and filtered in memory)");
        Ensure(
            pqNonkeyCap == filteredScanCap,
            $"a SELECT with no partition-key equality must be planned as a full table Scan and bill what the equivalent filtered Scan billed: PartiQL {Py.Repr(pqNonkeyCap)} vs Scan {Py.Repr(filteredScanCap)} (a single-partition Query bills {Py.Repr(queryCap)}, so a PartiQL number near that means the non-key predicate was resolved through an index that does not exist)");
        Ensure(
            pqNonkeyCap > pqKeyCap,
            $"the two statements return identical items and must nevertheless cost differently, because they are different plans: WHERE pk billed {Py.Repr(pqKeyCap)} and WHERE tag billed {Py.Repr(pqNonkeyCap)}");
    }
}

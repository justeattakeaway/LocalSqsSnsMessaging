// Translated from behavioral/dynamodb/check_query_indexes_capacity_sums_bytes_once_across_table_gsi_and_lsi.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-indexes-capacity-sums-bytes-once-across-table-gsi-and-lsi</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryIndexesCapacitySumsBytesOnceAcrossTableGsiAndLsi
{
    private const string Check = "query-indexes-capacity-sums-bytes-once-across-table-gsi-and-lsi";
    private const string Gsi = "gsi1";
    private const string Lsi = "lsi1";

    // Three items landing on exact 1 KB / 5 KB / 9 KB raw byte totals -- all inside the 1-9 KB band --
    // chosen so the *sum-then-round-once* rule (ceil(total_bytes / 4096)) disagrees with a per-item
    // ceil-then-sum bug: ceil(1024/4096)+ceil(5120/4096)+ceil(9216/4096) = 1+2+3 = 6, but the documented
    // rule rounds the SUM once: ceil(15360/4096) = 4. A target that rounds per item instead of once
    // over the total reports 6 (strong) / 3.0 (eventual) here, not 4 / 2.0.
    private static readonly int[] Sizes = [1024, 5120, 9216];
    private const string Pk = "P";
    private const string Gpk = "G";

    // Fixed-length attribute names/values whose byte cost (len(name) + len(value)) is identical on
    // every item, so only the payload length controls each item's total size.
    private static readonly int FixedOverhead = ("pk".Length + Pk.Length) + ("gpk".Length + Gpk.Length);
    private static readonly int RangeOverhead = ("sk".Length + 1) + ("lsk".Length + 1);
    private static readonly int PayloadNameBytes = "payload".Length;

    private static Dictionary<string, AttributeValue> MakeItem(int i, int size)
    {
        var digit = i.ToString(CultureInfo.InvariantCulture);
        var payloadLen = size - FixedOverhead - RangeOverhead - PayloadNameBytes;
        Ensure(payloadLen > 0, $"size {size} too small for the fixed attribute overhead");
        return Item(
            ("pk", S(Pk)),
            ("sk", S(digit)),
            ("gpk", S(Gpk)),
            ("lsk", S(digit)),
            ("payload", S(new string('x', payloadLen))));
    }

    private static readonly int TotalBytes = Sizes.Sum();
    private static readonly int StrongRcu = (TotalBytes + 4095) / 4096; // ceil(total / 4096), rounding the SUM once
    private static readonly double EventualRcu = StrongRcu / 2.0;

    /// <summary>A poll's timeout: capS, bounded by the runner's remaining time less 5 s.</summary>
    private static double BudgetS(FidelityContext ctx, double capS)
    {
        var left = ctx.TimeLeftSeconds;
        return Math.Max(1.0, Math.Min(capS, left - 5.0));
    }

    /// <summary>
    /// Poll <paramref name="fn"/> (a Query call) until it returns <paramref name="want"/> items; a GSI write is eventually
    /// consistent, so the fan-out needs to land before capacity is measured against it.
    /// </summary>
    private static async Task<QueryResponse> QuerySettled(FidelityContext ctx, Func<Task<QueryResponse>> fn, int want, string description, double capS = 90.0)
    {
        var clock = FidelityTime.StartNew();
        var budgetS = BudgetS(ctx, capS);
        var deadline = TimeSpan.FromSeconds(budgetS);
        while (true)
        {
            var resp = await fn();
            if (resp.Count >= want)
            {
                return resp;
            }

            if (clock.Elapsed >= deadline)
            {
                throw Fail(
                    $"{description} did not settle to {want} item(s) within {budgetS.ToString("F0", CultureInfo.InvariantCulture)}s; " +
                    $"last Count={Py.Repr(resp.Count)}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1.0));
        }
    }

    /// <summary>A <c>Capacity</c> as boto3 hands it back: a dict of the members present.</summary>
    private static Dictionary<string, object?>? Boto(Capacity? capacity)
    {
        if (capacity is null)
        {
            return null;
        }

        var output = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (capacity.ReadCapacityUnits is { } read)
        {
            output["ReadCapacityUnits"] = read;
        }

        if (capacity.WriteCapacityUnits is { } write)
        {
            output["WriteCapacityUnits"] = write;
        }

        if (capacity.CapacityUnits is { } units)
        {
            output["CapacityUnits"] = units;
        }

        return output;
    }

    private static Dictionary<string, object?>? Boto(Dictionary<string, Capacity>? indexes) =>
        indexes?.ToDictionary(e => e.Key, e => (object?)Boto(e.Value), StringComparer.Ordinal);

    /// <summary><c>sorted(cc)</c>: the names of the ConsumedCapacity members present.</summary>
    private static List<string> Members(ConsumedCapacity cc)
    {
        var members = new List<string>();
        if (cc.TableName is not null)
        {
            members.Add("TableName");
        }

        if (cc.CapacityUnits is not null)
        {
            members.Add("CapacityUnits");
        }

        if (cc.ReadCapacityUnits is not null)
        {
            members.Add("ReadCapacityUnits");
        }

        if (cc.WriteCapacityUnits is not null)
        {
            members.Add("WriteCapacityUnits");
        }

        if (cc.Table is not null)
        {
            members.Add("Table");
        }

        if (cc.LocalSecondaryIndexes is not null)
        {
            members.Add("LocalSecondaryIndexes");
        }

        if (cc.GlobalSecondaryIndexes is not null)
        {
            members.Add("GlobalSecondaryIndexes");
        }

        members.Sort(StringComparer.Ordinal);
        return members;
    }

    /// <summary>
    /// ReturnConsumedCapacity=INDEXES on a Query bills the SUM of the examined items' raw bytes,
    /// rounded to the 4 KB boundary ONCE, identically whether answered by the base table, an
    /// ALL-projection LSI or GSI, and strongly or eventually consistent.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/CapacityUnitCalculations.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-indexes-capacity-sums-bytes-once-across-table-gsi-and-lsi")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ccqix-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("gpk", ScalarAttributeType.S),
                new AttributeDefinition("lsk", ScalarAttributeType.S),
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = Gsi,
                    KeySchema = [new KeySchemaElement("gpk", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            LocalSecondaryIndexes =
            [
                new LocalSecondaryIndex
                {
                    IndexName = Lsi,
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("lsk", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 30);

        var items = Sizes.Select((size, i) => MakeItem(i, size)).ToList();
        foreach (var item in items)
        {
            await ddb.PutItemAsync(table, item);
        }

        // --- the base table, strongly consistent: the ground-truth sum-once rounding ---
        var tableStrong = await QuerySettled(
            ctx,
            () => ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                KeyConditionExpression = "pk = :p",
                ExpressionAttributeValues = Item((":p", S(Pk))),
                ConsistentRead = true,
                ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES,
            }),
            want: items.Count,
            description: "base-table strong Query");
        Ensure(tableStrong.LastEvaluatedKey is null, "this check compares single-page capacity; the base-table Query must not paginate at this size");
        var cc = tableStrong.ConsumedCapacity;
        Ensure(
            cc.CapacityUnits == StrongRcu,
            $"3 items totalling {TotalBytes} raw bytes queried strongly consistently from the base " +
            $"table must bill exactly ceil({TotalBytes}/4096) = {Py.Repr(StrongRcu)} RCU, got " +
            $"{Py.Repr(cc.CapacityUnits)} -- a per-item-rounded responder would report " +
            $"{Py.Repr(Sizes.Sum(s => (s + 4095) / 4096))} instead");
        Ensure(
            cc.Table?.CapacityUnits == StrongRcu,
            $"the Table arm of the breakdown must carry the same {Py.Repr(StrongRcu)} RCU as the aggregate, got {Py.Repr(Boto(cc.Table))}");
        Ensure(
            cc.GlobalSecondaryIndexes is null && cc.LocalSecondaryIndexes is null,
            $"a Query that names no IndexName must carry no per-index breakdown member, got {Py.Repr(Members(cc))}");

        // --- the base table, eventually consistent: same bytes, half the units ---
        var tableEventual = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :p",
            ExpressionAttributeValues = Item((":p", S(Pk))),
            ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES,
        });
        cc = tableEventual.ConsumedCapacity;
        Ensure(
            cc.CapacityUnits == EventualRcu,
            $"the identical read at eventual consistency must bill exactly half of {Py.Repr(StrongRcu)}, {Py.Repr(EventualRcu)}, got {Py.Repr(cc.CapacityUnits)}");
        Ensure(
            cc.Table?.CapacityUnits == EventualRcu,
            $"the Table arm must also halve to {Py.Repr(EventualRcu)}, got {Py.Repr(Boto(cc.Table))}");

        // --- the LSI, strongly consistent: an LSI shares the base partition, so ConsistentRead=True
        //     is legal and must reproduce the base table's own strong number exactly ---
        var lsiStrong = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = Lsi,
            KeyConditionExpression = "pk = :p",
            ExpressionAttributeValues = Item((":p", S(Pk))),
            ConsistentRead = true,
            ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES,
        });
        Ensure(
            lsiStrong.Count == items.Count,
            $"an ALL-projection LSI over the same partition must return all {items.Count} items, got {Py.Repr(lsiStrong.Count)}");
        cc = lsiStrong.ConsumedCapacity;
        Ensure(
            cc.CapacityUnits == StrongRcu,
            $"the same 3 items, served by the ALL-projection LSI at strong consistency, must bill " +
            $"exactly {Py.Repr(StrongRcu)} RCU -- identical to the base-table number -- got {Py.Repr(cc.CapacityUnits)}");
        Ensure(
            cc.LocalSecondaryIndexes[Lsi].CapacityUnits == StrongRcu,
            $"the LocalSecondaryIndexes[{Py.Repr(Lsi)}] arm must carry {Py.Repr(StrongRcu)} RCU, got {Py.Repr(Boto(cc.LocalSecondaryIndexes))}");
        Ensure(
            cc.Table.CapacityUnits == 0,
            $"an LSI-served Query reads no separate base-table copy and must charge the Table arm nothing, got {Py.Repr(Boto(cc.Table))}");

        // --- the LSI, eventually consistent: same bytes, half the units ---
        var lsiEventual = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = Lsi,
            KeyConditionExpression = "pk = :p",
            ExpressionAttributeValues = Item((":p", S(Pk))),
            ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES,
        });
        cc = lsiEventual.ConsumedCapacity;
        Ensure(
            cc.CapacityUnits == EventualRcu,
            $"the LSI read at eventual consistency must halve to {Py.Repr(EventualRcu)}, got {Py.Repr(cc.CapacityUnits)}");
        Ensure(
            cc.LocalSecondaryIndexes[Lsi].CapacityUnits == EventualRcu,
            $"the LocalSecondaryIndexes[{Py.Repr(Lsi)}] arm must also halve to {Py.Repr(EventualRcu)}, got {Py.Repr(Boto(cc.LocalSecondaryIndexes))}");

        // --- the GSI: always eventually consistent, same items via the gpk partition ---
        var gsiEventual = await QuerySettled(
            ctx,
            () => ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                IndexName = Gsi,
                KeyConditionExpression = "gpk = :g",
                ExpressionAttributeValues = Item((":g", S(Gpk))),
                ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES,
            }),
            want: items.Count,
            description: "GSI Query fan-out");
        Ensure(gsiEventual.LastEvaluatedKey is null, "this check compares single-page capacity; the GSI Query must not paginate at this size");
        cc = gsiEventual.ConsumedCapacity;
        Ensure(
            cc.CapacityUnits == EventualRcu,
            $"the same 3 items, served by the ALL-projection GSI (necessarily eventually consistent), " +
            $"must bill exactly {Py.Repr(EventualRcu)} RCU -- identical to the eventual base-table and LSI " +
            $"numbers -- got {Py.Repr(cc.CapacityUnits)}");
        Ensure(
            cc.GlobalSecondaryIndexes[Gsi].CapacityUnits == EventualRcu,
            $"the GlobalSecondaryIndexes[{Py.Repr(Gsi)}] arm must carry {Py.Repr(EventualRcu)} RCU, got {Py.Repr(Boto(cc.GlobalSecondaryIndexes))}");
        Ensure(
            cc.Table.CapacityUnits == 0,
            $"a GSI-served Query must charge the Table arm nothing, got {Py.Repr(Boto(cc.Table))}");
    }
}

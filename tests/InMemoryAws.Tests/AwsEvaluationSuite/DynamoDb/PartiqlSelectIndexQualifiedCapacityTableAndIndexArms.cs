// Translated from behavioral/dynamodb/check_partiql_select_index_qualified_capacity_table_and_index_arms.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-select-index-qualified-capacity-table-and-index-arms</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlSelectIndexQualifiedCapacityTableAndIndexArms
{
    private const string CheckId = "partiql-select-index-qualified-capacity-table-and-index-arms";
    private const string Gsi = "gsi1";
    private const string LsiKeys = "lsi-keys";
    private const string LsiInc = "lsi-inc";
    private const int PayloadLen = 50;

    // Every item is far below 4 KB. An index read rounds the summed size of the rows it reads up to
    // 4 KB: 1 RCU strongly consistent, 0.5 eventually consistent. A fetch back into the base table
    // rounds each fetched item up to 4 KB on its own: 1 RCU per item strongly consistent.
    private const double IndexRcuStrong = 1.0;
    private const double IndexRcuEventual = 0.5;
    private const double FetchRcuStrongPerItem = 1.0;

    /// <summary>A Capacity as boto3 hands it to the check: <c>{'CapacityUnits': 1.0}</c>.</summary>
    private static Dictionary<string, object?> Cap(Capacity? capacity)
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (capacity?.ReadCapacityUnits is { } r)
        {
            d["ReadCapacityUnits"] = r;
        }

        if (capacity?.WriteCapacityUnits is { } w)
        {
            d["WriteCapacityUnits"] = w;
        }

        if (capacity?.CapacityUnits is { } c)
        {
            d["CapacityUnits"] = c;
        }

        return d;
    }

    private static Dictionary<string, object?>? Caps(Dictionary<string, Capacity>? arms) =>
        arms?.ToDictionary(a => a.Key, a => (object?)Cap(a.Value), StringComparer.Ordinal);

    private static Dictionary<string, object?> Cc(ConsumedCapacity cc)
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal) { ["TableName"] = cc.TableName, ["CapacityUnits"] = cc.CapacityUnits };
        if (cc.Table is not null)
        {
            d["Table"] = Cap(cc.Table);
        }

        if (cc.LocalSecondaryIndexes is not null)
        {
            d["LocalSecondaryIndexes"] = Caps(cc.LocalSecondaryIndexes);
        }

        if (cc.GlobalSecondaryIndexes is not null)
        {
            d["GlobalSecondaryIndexes"] = Caps(cc.GlobalSecondaryIndexes);
        }

        return d;
    }

    /// <summary>Poll DescribeTable until the table and the named GSI are both ACTIVE.</summary>
    private static async Task<TableDescription> WaitTableAndGsiActive(FidelityContext ctx, IAmazonDynamoDB ddb, string tableName, string gsiName)
    {
        var budgetS = Math.Max(5.0, Math.Min(180.0, ctx.TimeLeftSeconds - 10.0));
        var clock = FidelityTime.StartNew();
        TableDescription? last = null;
        while (clock.Elapsed.TotalSeconds < budgetS)
        {
            last = (await ddb.DescribeTableAsync(tableName)).Table;
            var gsis = (last.GlobalSecondaryIndexes ?? []).ToDictionary(i => i.IndexName, i => i.IndexStatus?.Value, StringComparer.Ordinal);
            if (last.TableStatus == TableStatus.ACTIVE && gsis.GetValueOrDefault(gsiName) == "ACTIVE")
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2.0));
        }

        throw Fail($"table/GSI {tableName}/{gsiName} never went ACTIVE in {Py.Repr(budgetS)}s: {last}");
    }

    /// <summary>
    /// Poll an ExecuteStatement call until it returns <paramref name="want"/> items (a GSI fan-out is eventually
    /// consistent, so it may need a moment to settle before capacity is measured against it).
    /// </summary>
    private static async Task<ExecuteStatementResponse> SelectSettled(FidelityContext ctx, Func<Task<ExecuteStatementResponse>> fn, int want, string description, double capS = 90.0)
    {
        var budgetS = Math.Max(1.0, Math.Min(capS, ctx.TimeLeftSeconds - 5.0));
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var resp = await fn();
            if ((resp.Items?.Count ?? 0) >= want)
            {
                return resp;
            }

            if (clock.Elapsed.TotalSeconds >= budgetS)
            {
                throw Fail($"{description} did not settle to {want} item(s) within {budgetS.ToString("F0", CultureInfo.InvariantCulture)}s; last count={resp.Items?.Count ?? 0}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1.0));
        }
    }

    /// <summary>
    /// A PartiQL SELECT through a GSI or LSI bills the index arm and nothing on the Table arm
    /// when every selected attribute is in the projection; naming one an INCLUDE LSI omits adds a
    /// Table arm, ConsistentRead doubles both, a non-matching filter does not shrink it, Limit does.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-select-index-qualified-capacity-table-and-index-arms")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-pqlcap-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("gpk", ScalarAttributeType.S),
                new AttributeDefinition("lsk_keys", ScalarAttributeType.N),
                new AttributeDefinition("lsk_inc", ScalarAttributeType.N),
            ],
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
                    IndexName = LsiKeys,
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("lsk_keys", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.KEYS_ONLY },
                },
                new LocalSecondaryIndex
                {
                    IndexName = LsiInc,
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("lsk_inc", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.INCLUDE, NonKeyAttributes = ["category"] },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await WaitTableAndGsiActive(ctx, ddb, table, Gsi);

        const int items = 3;
        for (var i = 0; i < items; i++)
        {
            var n = i.ToString(CultureInfo.InvariantCulture);
            await ddb.PutItemAsync(table, Item(
                ("pk", S("P")), ("sk", S(n)), ("gpk", S("G")),
                ("lsk_keys", N(n)), ("lsk_inc", N(n)),
                ("category", S("cat")), ("extra", S(new string('x', PayloadLen)))));
        }

        // A keyed GSI read charges the GSI arm and nothing on the Table arm.
        var gsiResp = await SelectSettled(
            ctx,
            () => ddb.ExecuteStatementAsync(new ExecuteStatementRequest
            {
                Statement = $"SELECT * FROM \"{table}\".\"{Gsi}\" WHERE gpk = ?",
                Parameters = [S("G")],
                ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES,
            }),
            want: items,
            description: "keyed GSI SELECT");
        var cc = gsiResp.ConsumedCapacity;
        Ensure(cc.Table.CapacityUnits == 0, $"a keyed GSI SELECT must charge the Table arm nothing, got {Py.Repr(Cap(cc.Table))}");
        Ensure(
            cc.GlobalSecondaryIndexes[Gsi].CapacityUnits == IndexRcuEventual,
            $"the GSI arm must carry the {Py.Repr(IndexRcuEventual)} RCU read, got {Py.Repr(Caps(cc.GlobalSecondaryIndexes))}");
        Ensure(cc.CapacityUnits == IndexRcuEventual, $"the keyed GSI SELECT must cost {Py.Repr(IndexRcuEventual)} RCU in total, got {Py.Repr(Cc(cc))}");

        // SELECT * against a KEYS_ONLY LSI likewise charges no Table arm: every selected attribute is
        // the table's or the index's own key.
        var keysOnly = await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"SELECT * FROM \"{table}\".\"{LsiKeys}\" WHERE pk = ?",
            Parameters = [S("P")],
            ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES,
            ConsistentRead = true,
        });
        cc = keysOnly.ConsumedCapacity;
        Ensure(keysOnly.Items.Count == items, $"expected all {items} items, got {keysOnly.Items.Count}");
        Ensure(cc.Table.CapacityUnits == 0, $"SELECT * on a KEYS_ONLY LSI must charge the Table arm nothing, got {Py.Repr(Cap(cc.Table))}");
        Ensure(
            cc.LocalSecondaryIndexes[LsiKeys].CapacityUnits == IndexRcuStrong,
            $"the LSI arm must carry the {Py.Repr(IndexRcuStrong)} RCU read, got {Py.Repr(Caps(cc.LocalSecondaryIndexes))}");
        Ensure(cc.CapacityUnits == IndexRcuStrong, $"SELECT * on a KEYS_ONLY LSI must cost {Py.Repr(IndexRcuStrong)} RCU in total, got {Py.Repr(Cc(cc))}");

        // Naming 'extra' -- not in the INCLUDE projection -- through the LSI adds a nonzero Table arm
        // for the reach-back into the base table, strongly consistent.
        var reachStrong = await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"SELECT pk, sk, extra FROM \"{table}\".\"{LsiInc}\" WHERE pk = ?",
            Parameters = [S("P")],
            ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES,
            ConsistentRead = true,
        });
        cc = reachStrong.ConsumedCapacity;
        Ensure(reachStrong.Items.Count == items, $"expected all {items} items, got {reachStrong.Items.Count}");
        var tableStrong = cc.Table.CapacityUnits;
        Ensure(
            tableStrong == items * FetchRcuStrongPerItem,
            $"naming an unprojected attribute through an LSI must add a Table arm of {Py.Repr(items * FetchRcuStrongPerItem)} RCU, one per fetched item, got {Py.Repr(Cap(cc.Table))}");
        var lsiStrong = cc.LocalSecondaryIndexes[LsiInc].CapacityUnits;
        Ensure(
            lsiStrong == IndexRcuStrong,
            $"the LSI arm of the reach-back must be {Py.Repr(IndexRcuStrong)} RCU, got {Py.Repr(Caps(cc.LocalSecondaryIndexes))}");

        // ConsistentRead doubles both the Table and the LSI arm of that same reach-back.
        var reachEventual = await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"SELECT pk, sk, extra FROM \"{table}\".\"{LsiInc}\" WHERE pk = ?",
            Parameters = [S("P")],
            ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES,
        });
        cc = reachEventual.ConsumedCapacity;
        Ensure(
            cc.Table.CapacityUnits == tableStrong / 2,
            $"eventual consistency must halve the Table arm from {Py.Repr(tableStrong)}, got {Py.Repr(Cap(cc.Table))}");
        Ensure(
            cc.LocalSecondaryIndexes[LsiInc].CapacityUnits == lsiStrong / 2,
            $"eventual consistency must halve the LSI arm from {Py.Repr(lsiStrong)}, got {Py.Repr(Caps(cc.LocalSecondaryIndexes))}");

        // A WHERE term on the projected 'category' attribute that matches nothing still walks every
        // row for the reach-back: the cost is identical to the unfiltered read above, not lower.
        var filtered = await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"SELECT pk, sk, extra FROM \"{table}\".\"{LsiInc}\" WHERE pk = ? AND category = ?",
            Parameters = [S("P"), S("no-such-category")],
            ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES,
            ConsistentRead = true,
        });
        Ensure(filtered.Items.Count == 0, $"the nonmatching filter must return no items, got {Py.Repr(filtered.Items)}");
        cc = filtered.ConsumedCapacity;
        Ensure(
            cc.Table.CapacityUnits == tableStrong,
            $"a filter that matches nothing must still cost the full reach-back {Py.Repr(tableStrong)} (rows walked, not rows returned), got {Py.Repr(Cap(cc.Table))}");
        Ensure(
            cc.LocalSecondaryIndexes[LsiInc].CapacityUnits == lsiStrong,
            $"the LSI arm of the filtered reach-back must match the unfiltered {Py.Repr(lsiStrong)}, got {Py.Repr(Caps(cc.LocalSecondaryIndexes))}");

        // Limit=1 examines and fetches only one row.
        var limited = await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"SELECT pk, sk, extra FROM \"{table}\".\"{LsiInc}\" WHERE pk = ?",
            Parameters = [S("P")],
            ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES,
            ConsistentRead = true,
            Limit = 1,
        });
        Ensure(limited.Items.Count == 1, $"Limit=1 must return exactly 1 item, got {Py.Repr(limited.Items)}");
        cc = limited.ConsumedCapacity;
        Ensure(
            cc.Table.CapacityUnits == FetchRcuStrongPerItem,
            $"Limit=1 must fetch one item for {Py.Repr(FetchRcuStrongPerItem)} RCU on the Table arm, got {Py.Repr(Cap(cc.Table))}");
        Ensure(
            cc.LocalSecondaryIndexes[LsiInc].CapacityUnits == IndexRcuStrong,
            $"Limit=1 must read {Py.Repr(IndexRcuStrong)} RCU on the LSI arm, got {Py.Repr(Caps(cc.LocalSecondaryIndexes))}");
    }
}

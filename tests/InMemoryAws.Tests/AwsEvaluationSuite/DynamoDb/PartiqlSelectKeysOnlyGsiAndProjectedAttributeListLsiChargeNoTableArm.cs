// Translated from behavioral/dynamodb/check_partiql_select_keys_only_gsi_and_projected_attribute_list_lsi_charge_no_table_arm.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-select-keys-only-gsi-and-projected-attribute-list-lsi-charge-no-table-arm</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlSelectKeysOnlyGsiAndProjectedAttributeListLsiChargeNoTableArm
{
    private const string CheckId = "partiql-select-keys-only-gsi-and-projected-attribute-list-lsi-charge-no-table-arm";
    private const string Gsi = "gsi-keys-only";
    private const string Lsi = "lsi-inc";

    // Every item is far below 4 KB, so an index read rounds the summed size of the rows it reads up to
    // 4 KB once: 1 RCU strongly consistent, 0.5 RCU eventually consistent.
    private const double IndexRcuStrong = 1.0;
    private const double IndexRcuEventual = 0.5;

    /// <summary>Poll DescribeTable until the table and the named GSI are both ACTIVE.</summary>
    private static async Task<TableDescription> WaitTableAndGsiActive(FidelityContext ctx, IAmazonDynamoDB ddb, string tableName, string gsiName)
    {
        var budgetS = Math.Max(5.0, Math.Min(180.0, ctx.TimeLeftSeconds - 10.0));
        var clock = FidelityTime.StartNew();
        TableDescription? last = null;
        while (clock.Elapsed.TotalSeconds < budgetS)
        {
            last = (await ddb.DescribeTableAsync(tableName)).Table;
            var gsis = (last.GlobalSecondaryIndexes ?? []).ToDictionary(i => i.IndexName, i => i.IndexStatus?.Value);
            if (last.TableStatus == TableStatus.ACTIVE && gsis.GetValueOrDefault(gsiName) == "ACTIVE")
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2.0));
        }

        throw Fail($"table/GSI {tableName}/{gsiName} never went ACTIVE in {budgetS}s: {last}");
    }

    /// <summary>
    /// Poll an ExecuteStatement call until it returns <paramref name="want"/> items (a GSI fan-out is
    /// eventually consistent, so it may need a moment to settle before capacity is measured against it).
    /// </summary>
    private static async Task<ExecuteStatementResponse> SelectSettled(FidelityContext ctx, Func<Task<ExecuteStatementResponse>> fn, int want, string description, double capS = 90.0)
    {
        var budgetS = Math.Max(1.0, Math.Min(capS, ctx.TimeLeftSeconds - 5.0));
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var resp = await fn();
            if ((resp.Items ?? []).Count == want)
            {
                return resp;
            }

            if (clock.Elapsed.TotalSeconds >= budgetS)
            {
                throw Fail($"{description} did not settle to {want} item(s) within {budgetS:F0}s; last count={(resp.Items ?? []).Count}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1.0));
        }
    }

    private static string CapacityRepr(Capacity? capacity) =>
        capacity is null ? "None" : $"{{'CapacityUnits': {Py.Repr(capacity.CapacityUnits)}}}";

    private static string CapacityRepr(Dictionary<string, Capacity>? capacities) =>
        capacities is null ? "None" : "{" + string.Join(", ", capacities.Select(c => $"{Py.Repr(c.Key)}: {CapacityRepr(c.Value)}")) + "}";

    private static string CapacityRepr(ConsumedCapacity cc) =>
        $"{{'TableName': {Py.Repr(cc.TableName)}, 'CapacityUnits': {Py.Repr(cc.CapacityUnits)}, 'Table': {CapacityRepr(cc.Table)}, "
        + $"'GlobalSecondaryIndexes': {CapacityRepr(cc.GlobalSecondaryIndexes)}, 'LocalSecondaryIndexes': {CapacityRepr(cc.LocalSecondaryIndexes)}}}";

    private static List<string> SortedKeys(Dictionary<string, AttributeValue> item) => [.. item.Keys.Order(StringComparer.Ordinal)];

    /// <summary>
    /// A PartiQL SELECT * through a KEYS_ONLY GSI charges nothing on the Table arm, and naming
    /// only an LSI's projected attributes (base keys plus its INCLUDE list) likewise charges nothing
    /// on the Table arm; both charge only their index arm.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/CapacityUnitCalculations.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-select-keys-only-gsi-and-projected-attribute-list-lsi-charge-no-table-arm")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-pqlproj-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("gpk", ScalarAttributeType.S),
                new AttributeDefinition("lsk", ScalarAttributeType.N),
            ],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = Gsi,
                    KeySchema = [new KeySchemaElement("gpk", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.KEYS_ONLY },
                },
            ],
            LocalSecondaryIndexes =
            [
                new LocalSecondaryIndex
                {
                    IndexName = Lsi,
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("lsk", KeyType.RANGE)],
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
                ("pk", S("P")), ("sk", S(n)), ("gpk", S("G")), ("lsk", N(n)),
                ("category", S("cat")), ("extra", S("unprojected-value"))));
        }

        // SELECT * against a KEYS_ONLY GSI charges no Table arm: every selected attribute -- the
        // table key and the GSI's own key -- is already in the index entry.
        var gsiResp = await SelectSettled(
            ctx,
            () => ddb.ExecuteStatementAsync(new ExecuteStatementRequest
            {
                Statement = $"SELECT * FROM \"{table}\".\"{Gsi}\" WHERE gpk = ?",
                Parameters = [S("G")],
                ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES,
            }),
            want: items,
            description: "keys-only GSI SELECT *");
        Ensure(gsiResp.Items.Count == items, $"expected all {items} items, got {gsiResp.Items.Count}");
        foreach (var item in gsiResp.Items)
        {
            Ensure(
                item.Keys.ToHashSet().SetEquals(["pk", "sk", "gpk"]),
                $"SELECT * through a KEYS_ONLY GSI must project only the table and index keys, got {Py.Repr(SortedKeys(item))}");
        }

        var cc = gsiResp.ConsumedCapacity;
        Ensure(cc.Table?.CapacityUnits == 0, $"SELECT * on a KEYS_ONLY GSI must charge the Table arm nothing, got {CapacityRepr(cc.Table)}");
        Ensure(
            cc.GlobalSecondaryIndexes?.GetValueOrDefault(Gsi)?.CapacityUnits == IndexRcuEventual,
            $"the GSI arm must carry the {Py.Repr(IndexRcuEventual)} RCU read, got {CapacityRepr(cc.GlobalSecondaryIndexes)}");
        Ensure(cc.CapacityUnits == IndexRcuEventual, $"SELECT * on a KEYS_ONLY GSI must cost {Py.Repr(IndexRcuEventual)} RCU in total, got {CapacityRepr(cc)}");

        // Naming only the LSI's projected attributes -- the table keys plus its INCLUDE list -- charges
        // no Table arm either: nothing selected needs a reach-back into the base table.
        var lsiResp = await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"SELECT pk, sk, category FROM \"{table}\".\"{Lsi}\" WHERE pk = ?",
            Parameters = [S("P")],
            ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES,
            ConsistentRead = true,
        });
        Ensure(lsiResp.Items.Count == items, $"expected all {items} items, got {lsiResp.Items.Count}");
        foreach (var item in lsiResp.Items)
        {
            Ensure(
                item.Keys.ToHashSet().SetEquals(["pk", "sk", "category"]),
                $"naming only the projected attributes must return exactly those, got {Py.Repr(SortedKeys(item))}");
            Ensure(Py.Equal(item["category"], S("cat")), $"expected category=cat, got {Py.Repr(item["category"])}");
        }

        cc = lsiResp.ConsumedCapacity;
        Ensure(cc.Table?.CapacityUnits == 0, $"naming only an LSI's projected attributes must charge the Table arm nothing, got {CapacityRepr(cc.Table)}");
        Ensure(
            cc.LocalSecondaryIndexes?.GetValueOrDefault(Lsi)?.CapacityUnits == IndexRcuStrong,
            $"the LSI arm must carry the {Py.Repr(IndexRcuStrong)} RCU read, got {CapacityRepr(cc.LocalSecondaryIndexes)}");
        Ensure(
            cc.CapacityUnits == IndexRcuStrong,
            $"naming only the projected attributes through the LSI must cost {Py.Repr(IndexRcuStrong)} RCU in total, got {CapacityRepr(cc)}");

        // Contrast: naming an attribute outside the INCLUDE list, through the same LSI and the same
        // keys, bills the Table arm one read per fetched item -- proving the zero above is a property
        // of the attribute list, not of the index or the read path.
        var reachBack = await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"SELECT pk, sk, extra FROM \"{table}\".\"{Lsi}\" WHERE pk = ?",
            Parameters = [S("P")],
            ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES,
            ConsistentRead = true,
        });
        Ensure(reachBack.Items.Count == items, $"expected all {items} items, got {reachBack.Items.Count}");
        Ensure(
            reachBack.ConsumedCapacity.Table?.CapacityUnits == IndexRcuStrong * items,
            $"naming 'extra', which the LSI does not project, must bill the Table arm one strongly "
            + $"consistent 4 KB read per fetched item, {Py.Repr(IndexRcuStrong * items)} RCU; got "
            + CapacityRepr(reachBack.ConsumedCapacity.Table));
    }
}

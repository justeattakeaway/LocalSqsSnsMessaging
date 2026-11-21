// Translated from behavioral/dynamodb/check_lsi_select_all_attributes_query_bills_base_table_per_item_across_multiple_items.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.lsi-select-all-attributes-query-bills-base-table-per-item-across-multiple-items</summary>
[Category("AwsEvaluationSuite")]
public sealed class LsiSelectAllAttributesQueryBillsBaseTablePerItemAcrossMultipleItems
{
    private const string Check = "lsi-select-all-attributes-query-bills-base-table-per-item-across-multiple-items";
    private const string Pk = "P";

    // Three items at distinct 1 KB / 5 KB / 9 KB raw-byte bands, each rounded to its own 4 KB
    // boundary: ceil(1024/4096) + ceil(5120/4096) + ceil(9216/4096) = 1 + 2 + 3 = 6.
    private static readonly int[] Sizes = [1024, 5120, 9216];

    // Fixed per-item overhead (attribute name + key value bytes) so only the payload length
    // controls each item's total size.
    private const int PkBytes = 2 + 1; // len('pk') + len(_PK)
    private const int RangeBytes = (2 + 1) + (3 + 1); // (len('sk') + 1) + (len('lsk') + 1)
    private const int PayloadNameBytes = 7; // len('payload')
    private const int ItemOverhead = PkBytes + RangeBytes + PayloadNameBytes;

    // A KEYS_ONLY LSI entry carries only the base table's key and the index's own sort key -- no
    // payload -- so its per-item size is fixed regardless of the payload length.
    private const int IndexOnlyItemBytes = PkBytes + RangeBytes;

    private static readonly int TableRcu = Sizes.Sum(s => (int)Math.Ceiling(s / 4096.0));
    private static readonly int TotalIndexOnlyBytes = IndexOnlyItemBytes * Sizes.Length;
    private static readonly int LsiRcu = (int)Math.Ceiling(TotalIndexOnlyBytes / 4096.0);

    private static Dictionary<string, AttributeValue> ItemOf(int i, int size)
    {
        var digit = i.ToString(CultureInfo.InvariantCulture);
        var payloadLen = size - ItemOverhead;
        Ensure(payloadLen > 0, $"size {size} too small for the fixed attribute overhead");
        return Item(("pk", S(Pk)), ("sk", S(digit)), ("lsk", S(digit)), ("payload", S(new string('x', payloadLen))));
    }

    /// <summary>
    /// A Query on a KEYS_ONLY LSI with Select=ALL_ATTRIBUTES fetches every matching item from the
    /// base table and bills the Table arm the SUM of each item's OWN 4 KB-rounded size, on top of the
    /// unchanged index-only LocalSecondaryIndexes arm; omitting Select charges the Table arm nothing.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.lsi-select-all-attributes-query-bills-base-table-per-item-across-multiple-items")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-lsiall-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("lsk", ScalarAttributeType.S),
            ],
            LocalSecondaryIndexes =
            [
                new LocalSecondaryIndex
                {
                    IndexName = "lsi1",
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("lsk", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.KEYS_ONLY },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        var items = Sizes.Select((size, i) => ItemOf(i, size)).ToList();
        foreach (var item in items)
        {
            await ddb.PutItemAsync(table, item);
        }

        // Positive control: no Select at all defaults to ALL_PROJECTED_ATTRIBUTES on an index query,
        // which the KEYS_ONLY projection satisfies without touching the base table.
        var bare = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = "lsi1",
            KeyConditionExpression = "pk = :p",
            ExpressionAttributeValues = Item((":p", S(Pk))),
            ConsistentRead = true,
            ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES,
        });
        Ensure(bare.Count == items.Count, $"all {Py.Repr(items.Count)} items must match the partition, got {Py.Repr(bare.Count)}");
        Ensure(bare.LastEvaluatedKey is not { Count: > 0 }, "this check compares single-page capacity; the Query must not paginate");
        foreach (var bareItem in bare.Items)
        {
            Ensure(
                bareItem.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(["pk", "sk", "lsk"]),
                $"a KEYS_ONLY LSI query with no Select must return only the keys, got {Py.Repr(bareItem.Keys.Order(StringComparer.Ordinal).ToList())}");
        }

        var bareCc = bare.ConsumedCapacity;
        Ensure(
            bareCc.CapacityUnits == LsiRcu,
            $"the index-only read of {Py.Repr(items.Count)} items totalling {Py.Repr(TotalIndexOnlyBytes)} raw " +
            $"bytes must bill exactly {Py.Repr(LsiRcu)} RCU, got {Py.Repr(bareCc.CapacityUnits)}");
        Ensure(
            (bareCc.Table?.CapacityUnits ?? 0) == 0,
            $"an index-only LSI read must charge the Table arm nothing, got {Py.Repr(Arm(bareCc.Table))}");
        Ensure(
            bareCc.LocalSecondaryIndexes["lsi1"].CapacityUnits == LsiRcu,
            $"the LocalSecondaryIndexes['lsi1'] arm must carry {Py.Repr(LsiRcu)} RCU, got " +
            $"{Py.Repr(Arms(bareCc.LocalSecondaryIndexes))}");

        // The behaviour under test: Select=ALL_ATTRIBUTES forces a base-table fetch of every matching
        // item, and the Table arm bills the sum of each item's own 4 KB-rounded size.
        var fetched = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = "lsi1",
            KeyConditionExpression = "pk = :p",
            Select = Select.ALL_ATTRIBUTES,
            ExpressionAttributeValues = Item((":p", S(Pk))),
            ConsistentRead = true,
            ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES,
        });
        Ensure(fetched.Count == items.Count, $"all {Py.Repr(items.Count)} items must match, got {Py.Repr(fetched.Count)}");
        Ensure(fetched.LastEvaluatedKey is not { Count: > 0 }, "this check compares single-page capacity; the Query must not paginate");
        var fetchedBySk = fetched.Items.ToDictionary(i => i["sk"].S, StringComparer.Ordinal);
        for (var idx = 0; idx < Sizes.Length; idx++)
        {
            var digit = idx.ToString(CultureInfo.InvariantCulture);
            var fetchedItem = fetchedBySk[digit];
            var expectedPayloadLen = Sizes[idx] - ItemOverhead;
            Ensure(
                fetchedItem.TryGetValue("payload", out var payload) && payload.S == new string('x', expectedPayloadLen),
                "Select=ALL_ATTRIBUTES must fetch the full item, including \"payload\", from the base " +
                $"table, got {Py.Repr(fetchedItem.Keys.Order(StringComparer.Ordinal).ToList())} for sk={Py.Repr(digit)}");
        }

        var fetchedCc = fetched.ConsumedCapacity;
        Ensure(
            fetchedCc.Table?.CapacityUnits == TableRcu,
            $"the base-table fetch of {Py.Repr(items.Count)} items sized ({string.Join(", ", Sizes)}) raw bytes must bill the " +
            "SUM of each item's own 4 KB-rounded size on the Table arm, " +
            $"{string.Join(" + ", Sizes.Select(s => (int)Math.Ceiling(s / 4096.0)))} = {Py.Repr(TableRcu)} RCU, got " +
            $"{Py.Repr(Arm(fetchedCc.Table))}");
        Ensure(
            fetchedCc.LocalSecondaryIndexes["lsi1"].CapacityUnits == LsiRcu,
            "the LocalSecondaryIndexes['lsi1'] arm must still carry the unchanged index-only " +
            $"{Py.Repr(LsiRcu)} RCU, got {Py.Repr(Arms(fetchedCc.LocalSecondaryIndexes))}");
        Ensure(
            fetchedCc.CapacityUnits == TableRcu + LsiRcu,
            "the total must be the base-table fetch PLUS the unchanged index-only read: " +
            $"{Py.Repr(TableRcu)} + {Py.Repr(LsiRcu)} = {Py.Repr(TableRcu + LsiRcu)}, got {Py.Repr(fetchedCc.CapacityUnits)}");
        Ensure(
            fetchedCc.CapacityUnits - bareCc.CapacityUnits == TableRcu,
            "the Select=ALL_ATTRIBUTES Query must exceed the index-only read by exactly the base " +
            $"items' per-item-rounded size, {Py.Repr(TableRcu)} RCU, got a difference of " +
            $"{Py.Repr(fetchedCc.CapacityUnits - bareCc.CapacityUnits)}");
    }

    /// <summary>A capacity arm as boto3 hands it back: <c>{'CapacityUnits': 6.0}</c>.</summary>
    private static Dictionary<string, object?>? Arm(Capacity? capacity) =>
        capacity is null ? null : new() { ["CapacityUnits"] = capacity.CapacityUnits };

    private static Dictionary<string, object?>? Arms(Dictionary<string, Capacity>? arms) =>
        arms?.ToDictionary(e => e.Key, e => (object?)Arm(e.Value), StringComparer.Ordinal);
}

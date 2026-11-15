// Translated from behavioral/dynamodb/check_transact_get_items_bills_missing_items_and_reports_indexes_capacity.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-get-items-bills-missing-items-and-reports-indexes-capacity</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactGetItemsBillsMissingItemsAndReportsIndexesCapacity
{
    /// <summary>A cleanup running undo() where a failed delete raises; in-use is retried every 2 s.</summary>
    private static Func<Task> Teardown(Func<Task> undo, string notFound, double timeoutSeconds = 300.0) => async () =>
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(undo);
            if (error is null || error.Code == notFound)
            {
                return;
            }

            if (error.Code != "ResourceInUseException" || clock.Elapsed.TotalSeconds >= timeoutSeconds)
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    };

    /// <summary>Poll DescribeTable every 2 s until the table and its indexes are ACTIVE.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var timeLeft = ctx.TimeLeftSeconds;
        var deadline = Math.Max(0.0, (timeLeft > 0 ? timeLeft : 300.0) - 30.0);
        var clock = FidelityTime.StartNew();
        TableDescription? desc = null;
        while (clock.Elapsed.TotalSeconds < deadline)
        {
            desc = (await ddb.DescribeTableAsync(table)).Table;
            if (desc.TableStatus == TableStatus.ACTIVE
                && (desc.GlobalSecondaryIndexes ?? []).All(g => g.IndexStatus == IndexStatus.ACTIVE))
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {desc}");
    }

    /// <summary>Create an on-demand table keyed pk (S), plus sk of rangeType when given; wait ACTIVE.</summary>
    private static async Task<string> CreateTable(FidelityContext ctx, IAmazonDynamoDB ddb, string prefix, ScalarAttributeType? rangeType = null)
    {
        var table = FidelityContext.UniqueName(prefix);
        var keys = new List<KeySchemaElement> { new("pk", KeyType.HASH) };
        var attrs = new List<AttributeDefinition> { new("pk", ScalarAttributeType.S) };
        if (rangeType is not null)
        {
            keys.Add(new KeySchemaElement("sk", KeyType.RANGE));
            attrs.Add(new AttributeDefinition("sk", rangeType));
        }

        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = keys,
            AttributeDefinitions = attrs,
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await WaitActive(ddb, ctx, table);
        return table;
    }

    /// <summary>The single ConsumedCapacity entry of a TransactGetItems response.</summary>
    private static ConsumedCapacity CapacityOf(TransactGetItemsResponse resp, string label)
    {
        var consumed = resp.ConsumedCapacity;
        Ensure(consumed is { Count: 1 }, $"{label}: ConsumedCapacity was {Py.Repr(consumed?.Select(CapacityDict).ToList())}");
        return consumed![0];
    }

    private static Dictionary<string, object?> CapacityDict(ConsumedCapacity c)
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (c.TableName is not null)
        {
            d["TableName"] = c.TableName;
        }

        if (c.CapacityUnits is not null)
        {
            d["CapacityUnits"] = c.CapacityUnits;
        }

        if (c.ReadCapacityUnits is not null)
        {
            d["ReadCapacityUnits"] = c.ReadCapacityUnits;
        }

        if (c.WriteCapacityUnits is not null)
        {
            d["WriteCapacityUnits"] = c.WriteCapacityUnits;
        }

        if (c.Table is not null)
        {
            d["Table"] = TableCapacityDict(c.Table);
        }

        if (c.GlobalSecondaryIndexes is not null)
        {
            d["GlobalSecondaryIndexes"] = c.GlobalSecondaryIndexes.ToDictionary(e => e.Key, e => TableCapacityDict(e.Value), StringComparer.Ordinal);
        }

        if (c.LocalSecondaryIndexes is not null)
        {
            d["LocalSecondaryIndexes"] = c.LocalSecondaryIndexes.ToDictionary(e => e.Key, e => TableCapacityDict(e.Value), StringComparer.Ordinal);
        }

        return d;
    }

    private static Dictionary<string, object?>? TableCapacityDict(Capacity? c)
    {
        if (c is null)
        {
            return null;
        }

        var d = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (c.CapacityUnits is not null)
        {
            d["CapacityUnits"] = c.CapacityUnits;
        }

        if (c.ReadCapacityUnits is not null)
        {
            d["ReadCapacityUnits"] = c.ReadCapacityUnits;
        }

        if (c.WriteCapacityUnits is not null)
        {
            d["WriteCapacityUnits"] = c.WriteCapacityUnits;
        }

        return d;
    }

    /// <summary>A Responses entry as boto3 hands it back: <c>{}</c> or <c>{'Item': ...}</c>.</summary>
    private static List<Dictionary<string, object?>> ResponsesOf(TransactGetItemsResponse resp) =>
        [.. resp.Responses.Select(r => r.Item is null ? new Dictionary<string, object?>() : new Dictionary<string, object?> { ["Item"] = r.Item })];

    /// <summary>
    /// TransactGetItems bills 2 read units per Get even when the item is missing, and INDEXES on a
    /// table without indexes reports read units for the table and no write units or index entries.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-get-items-bills-missing-items-and-reports-indexes-capacity")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await CreateTable(ctx, ddb, "sim2real-behav-ddb-txrcu-");
        var present = Item(("pk", S("present")), ("v", S("small")));
        await ddb.PutItemAsync(table, present);
        TransactGetItem PresentGet() => new() { Get = new Get { TableName = table, Key = Item(("pk", S("present"))) } };
        TransactGetItem MissingGet() => new() { Get = new Get { TableName = table, Key = Item(("pk", S("missing"))) } };
        var empty = new Dictionary<string, object?>();
        var withPresent = new Dictionary<string, object?> { ["Item"] = present };

        var alone = await ddb.TransactGetItemsAsync(new TransactGetItemsRequest { TransactItems = [MissingGet()], ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL });
        Ensure(Py.Equal(ResponsesOf(alone), new[] { empty }), $"a Get of a missing key returned {Py.Repr(ResponsesOf(alone))}");
        var cap = CapacityOf(alone, "missing alone");
        Ensure(
            (cap.TableName, cap.CapacityUnits, cap.ReadCapacityUnits) == (table, 2.0, 2.0),
            $"a Get of a missing key consumed {Py.Repr(CapacityDict(cap))}, expected 2.0 read units");

        var pair = await ddb.TransactGetItemsAsync(new TransactGetItemsRequest { TransactItems = [PresentGet(), MissingGet()], ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL });
        Ensure(Py.Equal(ResponsesOf(pair), new[] { withPresent, empty }), $"present+missing returned {Py.Repr(ResponsesOf(pair))}");
        cap = CapacityOf(pair, "present+missing");
        Ensure(
            (cap.TableName, cap.CapacityUnits, cap.ReadCapacityUnits) == (table, 4.0, 4.0),
            $"present+missing consumed {Py.Repr(CapacityDict(cap))}, expected 4.0 read units");

        var indexed = await ddb.TransactGetItemsAsync(new TransactGetItemsRequest { TransactItems = [PresentGet()], ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES });
        Ensure(Py.Equal(ResponsesOf(indexed), new[] { withPresent }), $"INDEXES Get returned {Py.Repr(ResponsesOf(indexed))}");
        cap = CapacityOf(indexed, "INDEXES");
        Ensure(
            (cap.TableName, cap.CapacityUnits, cap.ReadCapacityUnits) == (table, 2.0, 2.0),
            $"INDEXES consumed {Py.Repr(CapacityDict(cap))}, expected 2.0 read units");
        Ensure(cap.WriteCapacityUnits is null, $"a transactional read reported write units: {Py.Repr(CapacityDict(cap))}");
        Ensure(
            Py.Equal(TableCapacityDict(cap.Table), new Dictionary<string, object?> { ["CapacityUnits"] = 2.0, ["ReadCapacityUnits"] = 2.0 }),
            $"INDEXES Table breakdown was {Py.Repr(TableCapacityDict(cap.Table))}");
        var stray = new List<string>();
        if (cap.GlobalSecondaryIndexes is not null)
        {
            stray.Add("GlobalSecondaryIndexes");
        }

        if (cap.LocalSecondaryIndexes is not null)
        {
            stray.Add("LocalSecondaryIndexes");
        }

        Ensure(stray.Count == 0, $"a table without indexes reported index capacity under {Py.Repr(stray)}: {Py.Repr(CapacityDict(cap))}");
    }
}

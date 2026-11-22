// Translated from behavioral/dynamodb/check_gsi_hash_only_query_orders_by_two_range_keys_sparse_range_item_excluded.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.gsi-hash-only-query-orders-by-two-range-keys-sparse-range-item-excluded</summary>
[Category("AwsEvaluationSuite")]
public sealed class GsiHashOnlyQueryOrdersByTwoRangeKeysSparseRangeItemExcluded
{
    private const string Check = "gsi-hash-only-query-orders-by-two-range-keys-sparse-range-item-excluded";
    private const string IndexName = "by-group";

    // (pk, gsk1, gsk2); p4 carries gsk1 but not gsk2, so it is missing one of the index's two RANGE
    // attributes and must be excluded from the index entirely, not merely unordered within it.
    private static readonly (string Pk, string Gsk1, string Gsk2)[] FullItems =
    [
        ("p1", "a", "x"),
        ("p2", "a", "y"),
        ("p3", "b", "x"),
        ("p5", "c", "y"),
    ];

    /// <summary>
    /// A HASH-only Query of a GSI keyed on one HASH and two RANGE attributes orders its items by
    /// the RANGE attributes in schema order, and excludes an item missing either one.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.gsi-hash-only-query-orders-by-two-range-keys-sparse-range-item-excluded")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-gsi1h2r-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("gpk", ScalarAttributeType.S),
                new AttributeDefinition("gsk1", ScalarAttributeType.S),
                new AttributeDefinition("gsk2", ScalarAttributeType.S),
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = IndexName,
                    KeySchema =
                    [
                        new KeySchemaElement("gpk", KeyType.HASH),
                        new KeySchemaElement("gsk1", KeyType.RANGE),
                        new KeySchemaElement("gsk2", KeyType.RANGE),
                    ],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await WaitActive(ddb, table);

        var desc = (await ddb.DescribeTableAsync(table)).Table;
        var keySchema = desc.GlobalSecondaryIndexes[0].KeySchema.Select(k => (k.AttributeName, k.KeyType.Value)).ToList();
        Ensure(
            keySchema.SequenceEqual([("gpk", "HASH"), ("gsk1", "RANGE"), ("gsk2", "RANGE")]),
            $"the GSI describes its key as {Py.Repr(keySchema.Select(k => new Dictionary<string, object?> { ["AttributeName"] = k.AttributeName, ["KeyType"] = k.Value }).ToList())}");

        foreach (var (pk, gsk1, gsk2) in FullItems)
        {
            await ddb.PutItemAsync(table, Item(("pk", S(pk)), ("gpk", S("g1")), ("gsk1", S(gsk1)), ("gsk2", S(gsk2))));
        }

        // p4 carries the HASH attribute and gsk1, but not gsk2: a sparse write relative to the index.
        await ddb.PutItemAsync(table, Item(("pk", S("p4")), ("gpk", S("g1")), ("gsk1", S("b"))));

        async Task<List<string>> Query()
        {
            var pks = new List<string>();
            Dictionary<string, AttributeValue>? start = null;
            do
            {
                var page = await ddb.QueryAsync(new QueryRequest
                {
                    TableName = table,
                    IndexName = IndexName,
                    KeyConditionExpression = "gpk = :g",
                    ExpressionAttributeValues = Item((":g", S("g1"))),
                    ExclusiveStartKey = start,
                });
                pks.AddRange(page.Items.Select(i => i["pk"].S));
                start = page.LastEvaluatedKey is { Count: > 0 } next ? next : null;
            }
            while (start is not null);
            return pks;
        }

        List<string> want = ["p1", "p2", "p3", "p5"];
        var clock = FidelityTime.StartNew();
        var deadline = Math.Max(0.0, ctx.TimeLeftSeconds - 60.0);
        List<string> got;
        while (true)
        {
            got = await Query();
            if (got.SequenceEqual(want) || clock.Elapsed.TotalSeconds >= deadline)
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        Ensure(
            got.SequenceEqual(want),
            $"the HASH-only query returned {Py.Repr(got)}, expected the four fully-keyed items ordered by " +
            $"(gsk1, gsk2) in schema order {Py.Repr(want)}, with the sparse item p4 excluded");

        async Task<List<string>> ScanGsi()
        {
            var pks = new List<string>();
            Dictionary<string, AttributeValue>? start = null;
            do
            {
                var page = await ddb.ScanAsync(new ScanRequest { TableName = table, IndexName = IndexName, ExclusiveStartKey = start });
                pks.AddRange(page.Items.Select(i => i["pk"].S));
                start = page.LastEvaluatedKey is { Count: > 0 } next ? next : null;
            }
            while (start is not null);
            return [.. pks.Order(StringComparer.Ordinal)];
        }

        var indexed = await ScanGsi();
        var sortedWant = want.Order(StringComparer.Ordinal).ToList();
        Ensure(indexed.SequenceEqual(sortedWant), $"a Scan of the GSI returned {Py.Repr(indexed)}, expected exactly {Py.Repr(sortedWant)}");
        Ensure(!indexed.Contains("p4"), $"p4, which lacks the index's gsk2 RANGE attribute, was indexed anyway: {Py.Repr(indexed)}");

        // p4 is a genuine table row even though the GSI never indexed it: GetItem on the base table
        // by the table's own primary key still returns it.
        var @base = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("p4"))), ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(@base["gsk1"], S("b")) && !@base.ContainsKey("gsk2"),
            $"base-table GetItem on p4 returned {Py.Repr(@base)}, expected gsk1='b' and no gsk2");
    }

    private static async Task WaitActive(IAmazonDynamoDB ddb, string table, double timeoutS = 300.0)
    {
        var clock = FidelityTime.StartNew();
        TableDescription? last = null;
        while (clock.Elapsed.TotalSeconds < timeoutS)
        {
            last = (await ddb.DescribeTableAsync(table)).Table;
            var indexes = last.GlobalSecondaryIndexes ?? [];
            if (last.TableStatus == TableStatus.ACTIVE && indexes.Count > 0 && indexes.All(g => g.IndexStatus == IndexStatus.ACTIVE))
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(3));
        }

        throw Fail($"table {table} or its GSI did not become ACTIVE: {Py.Repr(last?.TableStatus)}");
    }

    /// <summary>A cleanup running <paramref name="undo"/> where a failed delete raises; only <paramref name="notFound"/> means gone.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };
}

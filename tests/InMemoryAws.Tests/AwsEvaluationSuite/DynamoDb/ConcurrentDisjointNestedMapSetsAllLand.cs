// Translated from behavioral/dynamodb/check_concurrent_disjoint_nested_map_sets_all_land.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.concurrent-disjoint-nested-map-sets-all-land</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConcurrentDisjointNestedMapSetsAllLand
{
    private const int Writers = 50;

    /// <summary>
    /// Concurrent UpdateItem calls that each SET a distinct key of one map attribute all succeed,
    /// and the map then holds every key with its value and the item's other attributes intact.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Expressions.UpdateExpressions.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.concurrent-disjoint-nested-map-sets-all-land")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await CreateTable(ctx, ddb, "sim2real-behav-ddb-nested-");
        Dictionary<string, AttributeValue> Key() => Item(("pk", S("doc")));
        await ddb.PutItemAsync(table, Item(("pk", S("doc")), ("d", new AttributeValue { M = [] }), ("keep", S("same"))));

        static string Two(int i) => i.ToString("00", CultureInfo.InvariantCulture);

        // SET d.k<i> = v<i>; return None or the error it raised.
        async Task<string?> Write(int i)
        {
            var error = await FidelityContext.CaptureError(() => ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = Key(),
                UpdateExpression = "SET #d.#t = :v",
                ExpressionAttributeNames = Names(("#d", "d"), ("#t", $"k{Two(i)}")),
                ExpressionAttributeValues = Item((":v", S($"v{Two(i)}"))),
            }));
            return error is null ? null : $"k{Two(i)}: {error.Code}: {Py.Repr(error.Message)}";
        }

        List<string?> errors;
        using (ctx.Concurrently())
        {
            errors = (await PoolMap(16, [.. Enumerable.Range(0, Writers)], Write)).Where(e => e is not null).ToList();
        }

        Ensure(errors.Count == 0, $"concurrent nested SETs failed: {Py.Repr(errors)}");

        var item = (await Get(ddb, table, Key()))!;
        var expected = Enumerable.Range(0, Writers).ToDictionary(i => $"k{Two(i)}", i => S($"v{Two(i)}"), StringComparer.Ordinal);
        var got = item["d"].M;
        var missing = expected.Keys.Except(got.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        Ensure(missing.Count == 0, $"{Py.Repr(missing.Count)} nested SETs were lost: {Py.Repr(missing)}");
        Ensure(Py.Equal(got, expected), $"the map holds {Py.Repr(got)}, expected {Py.Repr(expected)}");
        Ensure(Py.Equal(item["keep"], S("same")), $"an unrelated attribute changed to {Py.Repr(item["keep"])}");
    }

    /// <summary>A cleanup running undo() where a failed delete raises; in-use is retried every 2 s.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        const double timeoutS = 300.0;
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(undo);
            if (error is null || notFound.Contains(error.Code))
            {
                return;
            }

            if (error.Code != "ResourceInUseException" || clock.Elapsed.TotalSeconds >= timeoutS)
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    };

    /// <summary>Poll DescribeTable every 2 s until the table and its indexes are ACTIVE.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var left = ctx.TimeLeftSeconds;
        var bound = Math.Max(0.0, (left == 0 ? 300.0 : left) - 30.0);
        var clock = FidelityTime.StartNew();
        TableDescription? desc = null;
        while (clock.Elapsed.TotalSeconds < bound)
        {
            desc = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = table })).Table;
            if (desc.TableStatus == TableStatus.ACTIVE && (desc.GlobalSecondaryIndexes ?? []).All(g => g.IndexStatus == IndexStatus.ACTIVE))
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {Py.Repr(desc?.TableStatus)}");
    }

    /// <summary>Create an on-demand table keyed pk (S), plus sk of range_type when given; wait ACTIVE.</summary>
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

    /// <summary>The item at key read with ConsistentRead, or None.</summary>
    private static async Task<Dictionary<string, AttributeValue>?> Get(IAmazonDynamoDB ddb, string table, Dictionary<string, AttributeValue> key) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;

    /// <summary><c>ThreadPoolExecutor(max_workers=workers).map(fn, items)</c>: results in order.</summary>
    private static async Task<TResult[]> PoolMap<T, TResult>(int workers, IReadOnlyList<T> items, Func<T, Task<TResult>> fn)
    {
        using var gate = new SemaphoreSlim(workers);
        return await Task.WhenAll(items.Select(item => Task.Run(async () =>
        {
            await gate.WaitAsync();
            try
            {
                return await fn(item);
            }
            finally
            {
                gate.Release();
            }
        })));
    }
}

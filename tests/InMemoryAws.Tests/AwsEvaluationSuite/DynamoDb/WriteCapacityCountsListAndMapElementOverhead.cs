// Translated from behavioral/dynamodb/check_write_capacity_counts_list_and_map_element_overhead.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.write-capacity-counts-list-and-map-element-overhead</summary>
[Category("AwsEvaluationSuite")]
public sealed class WriteCapacityCountsListAndMapElementOverhead
{
    /// <summary>
    /// PutItem and UpdateItem bill list- and map-heavy items by their size with per-element, per-entry
    /// and container overhead: exact CapacityUnits 2/5/9 for dense lists, 5, 4 and 5 for nested shapes.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.write-capacity-counts-list-and-map-element-overhead")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-lmcu-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await WaitActive(ddb, ctx, table);

        var cases = new (string Pk, Dictionary<string, AttributeValue> Attrs, double Want)[]
        {
            ("scalar", Item(("n", N("1")), ("s", S("hello"))), 1.0),
            ("l500", Item(("data", L(Repeat(() => S("aa"), 500)))), 2.0),
            ("l1500", Item(("data", L(Repeat(() => S("aa"), 1500)))), 5.0),
            ("l3000", Item(("data", L(Repeat(() => S("aa"), 3000)))), 9.0),
            ("m800", Item(("data", M([.. Enumerable.Range(0, 800).Select(i => ($"k{i.ToString(CultureInfo.InvariantCulture)}", S("v")))]))), 5.0),
            ("lofm", Item(("data", L(Repeat(() => M(("a", S("b"))), 500)))), 4.0),
            ("mofl", Item(("data", M([.. Enumerable.Range(0, 500).Select(i => ($"k{i.ToString(CultureInfo.InvariantCulture)}", L(S("b"))))]))), 5.0),
        };
        foreach (var (pk, attrs, want) in cases)
        {
            var item = Item(("pk", S(pk)));
            foreach (var (name, value) in attrs)
            {
                item[name] = value;
            }

            var resp = await ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = item, ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL });
            var cu = resp.ConsumedCapacity?.CapacityUnits;
            Ensure(cu == want, $"PutItem of {Py.Repr(pk)} consumed {Py.Repr(cu)} CapacityUnits, expected {Py.Repr(want)}");
        }

        var updated = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("upd"))),
            UpdateExpression = "SET #d = :v",
            ExpressionAttributeNames = Names(("#d", "data")),
            ExpressionAttributeValues = Item((":v", L(Repeat(() => S("aa"), 1500)))),
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        });
        var updatedCu = updated.ConsumedCapacity?.CapacityUnits;
        Ensure(updatedCu == 5.0, $"UpdateItem SET of a 1500-element list consumed {Py.Repr(updatedCu)} CapacityUnits, expected 5.0");

        var stored = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("upd"))), ConsistentRead = true })).Item ?? [];
        var count = stored.TryGetValue("data", out var data) ? data.L?.Count ?? 0 : 0;
        Ensure(count == 1500, $"the updated item reads back a list of {count} elements, expected 1500");
        stored = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("m800"))), ConsistentRead = true })).Item ?? [];
        count = stored.TryGetValue("data", out data) ? data.M?.Count ?? 0 : 0;
        Ensure(count == 800, $"the 800-entry map reads back with {count} entries");
    }

    private static AttributeValue[] Repeat(Func<AttributeValue> element, int count) => [.. Enumerable.Range(0, count).Select(_ => element())];

    /// <summary>A cleanup running undo() where a failed delete raises; in-use is retried every 2 s.</summary>
    private static Func<Task> Teardown(Func<Task> undo, string notFound, double timeoutS = 300.0) => async () =>
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(undo);
            if (error is null || error.Code == notFound)
            {
                return;
            }

            if (error.Code != "ResourceInUseException" || clock.Elapsed >= TimeSpan.FromSeconds(timeoutS))
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
        var deadline = TimeSpan.FromSeconds(Math.Max(0.0, (left != 0 ? left : 300.0) - 30.0));
        var clock = FidelityTime.StartNew();
        TableDescription? desc = null;
        while (clock.Elapsed < deadline)
        {
            desc = (await ddb.DescribeTableAsync(table)).Table;
            if (desc.TableStatus == TableStatus.ACTIVE && (desc.GlobalSecondaryIndexes ?? []).All(g => g.IndexStatus == IndexStatus.ACTIVE))
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {desc?.TableStatus?.Value}");
    }
}

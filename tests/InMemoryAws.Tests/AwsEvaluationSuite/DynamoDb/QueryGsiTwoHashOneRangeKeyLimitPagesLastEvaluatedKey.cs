// Translated from behavioral/dynamodb/check_query_gsi_two_hash_one_range_key_limit_pages_last_evaluated_key.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-gsi-two-hash-one-range-key-limit-pages-last-evaluated-key</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryGsiTwoHashOneRangeKeyLimitPagesLastEvaluatedKey
{
    private const string Check = "query-gsi-two-hash-one-range-key-limit-pages-last-evaluated-key";
    private const string IndexName = "by-tenant-region";
    private const int Limit = 5;
    private const int NItems = 13;

    /// <summary>
    /// A Limit-paged Query of a GSI keyed on two HASH attributes and one RANGE attribute pages
    /// with LastEvaluatedKey carrying the table's key plus all three index key attributes.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-gsi-two-hash-one-range-key-limit-pages-last-evaluated-key")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-gsi2h1r-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("tenant", ScalarAttributeType.S),
                new AttributeDefinition("region", ScalarAttributeType.S),
                new AttributeDefinition("ts", ScalarAttributeType.S),
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
                        new KeySchemaElement("tenant", KeyType.HASH),
                        new KeySchemaElement("region", KeyType.HASH),
                        new KeySchemaElement("ts", KeyType.RANGE),
                    ],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await WaitActive(ddb, table);

        for (var i = 0; i < NItems; i++)
        {
            await ddb.PutItemAsync(table, Item(("pk", S($"item-{i:000}")), ("tenant", S("acme")), ("region", S("eu")), ("ts", S($"{i:000}"))));
        }

        // One item outside the HASH pair, so the index holds more than the partition under test.
        await ddb.PutItemAsync(table, Item(("pk", S("item-other")), ("tenant", S("acme")), ("region", S("us")), ("ts", S("000"))));

        // The GSI is eventually consistent: poll the unpaged read until all 13 items are visible.
        var clock = FidelityTime.StartNew();
        var deadline = Math.Max(0.0, ctx.TimeLeftSeconds - 60.0);
        QueryResponse unpaged;
        while (true)
        {
            unpaged = await Query(ddb, table);
            if (unpaged.Items.Count >= NItems)
            {
                break;
            }

            Ensure(
                clock.Elapsed.TotalSeconds < deadline,
                $"the index never held all {NItems} items; last read had {unpaged.Items.Count}");
            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        Ensure(unpaged.Items.Count == NItems, $"expected exactly {NItems} items under tenant=acme/region=eu, got {unpaged.Items.Count}");
        var tss = unpaged.Items.Select(i => i["ts"].S).ToList();
        Ensure(tss.SequenceEqual(tss.Order(StringComparer.Ordinal)), $"unpaged GSI Query was not in ascending ts order: {Py.Repr(tss)}");

        // Walk the same query with Limit, following LastEvaluatedKey.
        var paged = new List<Dictionary<string, AttributeValue>>();
        Dictionary<string, AttributeValue>? start = null;
        Dictionary<string, AttributeValue>? firstLek = null;
        var pages = 0;
        while (true)
        {
            var page = await Query(ddb, table, limit: Limit, start: start);
            var items = page.Items;
            Ensure(items.Count <= Limit, $"page {pages} held {items.Count} items, more than Limit={Limit}");
            paged.AddRange(items);
            pages++;
            Ensure(pages <= 20, "LastEvaluatedKey never ran out after 20 pages");
            start = page.LastEvaluatedKey is { Count: > 0 } next ? next : null;
            if (pages == 1)
            {
                firstLek = start;
            }

            if (start is null)
            {
                break;
            }

            Ensure(
                start.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(["pk", "tenant", "region", "ts"]),
                $"page {pages}'s LastEvaluatedKey carries {Py.Repr(start.Keys.Order(StringComparer.Ordinal).ToList())}, expected exactly the " +
                "table's pk and the index's tenant+region+ts");
            Ensure(
                Py.Equal(start, KeyOf(items[^1])),
                $"page {pages}'s LastEvaluatedKey {Py.Repr(start)} does not name its own last item " +
                $"{Py.Repr(KeyOf(items[^1]))}");
        }

        var expectedPages = (NItems + Limit - 1) / Limit;
        Ensure(pages == expectedPages, $"a Limit={Limit} walk of {NItems} items took {pages} pages, expected {expectedPages}");
        Ensure(
            Py.Equal(paged, unpaged.Items),
            $"the concatenated paged walk {Py.Repr(paged)} differs from the unpaged read {Py.Repr(unpaged.Items)}");

        // Resuming from the first page's token alone returns exactly the items still owed.
        Ensure(firstLek is not null, "expected a LastEvaluatedKey after the first Limit=5 page");
        var rest = await Query(ddb, table, start: firstLek);
        var owed = unpaged.Items.Skip(Limit).ToList();
        Ensure(
            Py.Equal(rest.Items, owed),
            $"resuming from the first page's LastEvaluatedKey returned {Py.Repr(rest.Items)}, expected " +
            $"exactly the {unpaged.Items.Count - Limit} remaining items {Py.Repr(owed)}");
        Ensure(
            rest.LastEvaluatedKey is not { Count: > 0 },
            "resuming from the first page straight to the end still carried a LastEvaluatedKey: " +
            $"{Py.Repr(rest.LastEvaluatedKey)}");

        // The GSI's answer is backed by real table rows: GetItem on the first and last paged item by
        // the table's own primary key returns the same tenant/region/ts the index reported.
        foreach (var item in new[] { unpaged.Items[0], unpaged.Items[^1] })
        {
            var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", item["pk"])), ConsistentRead = true })).Item;
            Ensure(
                Py.Equal(got["tenant"], item["tenant"]) && Py.Equal(got["region"], item["region"]) && Py.Equal(got["ts"], item["ts"]),
                $"GetItem on {Py.Repr(item["pk"])} returned tenant={Py.Repr(got["tenant"])} region={Py.Repr(got["region"])} " +
                $"ts={Py.Repr(got["ts"])}, expected the GSI's own tenant={Py.Repr(item["tenant"])} region={Py.Repr(item["region"])} " +
                $"ts={Py.Repr(item["ts"])}");
        }
    }

    private static Task<QueryResponse> Query(IAmazonDynamoDB ddb, string table, int? limit = null, Dictionary<string, AttributeValue>? start = null) =>
        ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = IndexName,
            KeyConditionExpression = "tenant = :t AND #r = :r",
            ExpressionAttributeNames = Names(("#r", "region")),
            ExpressionAttributeValues = Item((":t", S("acme")), (":r", S("eu"))),
            Limit = limit,
            ExclusiveStartKey = start,
        });

    private static Dictionary<string, AttributeValue> KeyOf(Dictionary<string, AttributeValue> item) =>
        Item(("pk", item["pk"]), ("tenant", item["tenant"]), ("region", item["region"]), ("ts", item["ts"]));

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

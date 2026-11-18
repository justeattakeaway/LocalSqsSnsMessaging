// Translated from behavioral/dynamodb/check_gsi_query_last_evaluated_key_carries_every_table_and_index_key_attribute.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.gsi-query-last-evaluated-key-carries-every-table-and-index-key-attribute</summary>
[Category("AwsEvaluationSuite")]
public sealed class GsiQueryLastEvaluatedKeyCarriesEveryTableAndIndexKeyAttribute
{
    private const string CheckId = "gsi-query-last-evaluated-key-carries-every-table-and-index-key-attribute";
    private const string Index = "StatusIndex";
    private const int Limit = 5;
    private const int NItems = 24;
    private static readonly string[] Customers = ["cust-01", "cust-02", "cust-03"];
    private static readonly string[] KeyAttributes = ["CustomerId", "OrderId", "Status", "OrderSeq"];

    private static async Task WaitActive(IAmazonDynamoDB ddb, string table, double timeoutSeconds = 180.0)
    {
        var clock = FidelityTime.StartNew();
        TableDescription? last = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            last = (await ddb.DescribeTableAsync(table)).Table;
            var indexes = last.GlobalSecondaryIndexes ?? [];
            if (last.TableStatus == TableStatus.ACTIVE && indexes.Count > 0 && indexes.All(g => g.IndexStatus == IndexStatus.ACTIVE))
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(3));
        }

        throw Fail($"table {table} or its index did not become ACTIVE: {Py.Repr(last)}");
    }

    private static Task<QueryResponse> QueryGroup(IAmazonDynamoDB ddb, string table, int? limit = null, Dictionary<string, AttributeValue>? start = null) =>
        ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = Index,
            KeyConditionExpression = "#s = :s",
            ExpressionAttributeNames = Names(("#s", "Status")),
            ExpressionAttributeValues = Item((":s", S("PLACED"))),
            Limit = limit,
            ExclusiveStartKey = start,
        });

    private static Dictionary<string, AttributeValue> KeyOf(Dictionary<string, AttributeValue> item) =>
        Item(("CustomerId", item["CustomerId"]), ("OrderId", item["OrderId"]), ("Status", item["Status"]), ("OrderSeq", item["OrderSeq"]));

    /// <summary>
    /// A Query against a GSI with a HASH+RANGE key, on a table with a HASH+RANGE primary key,
    /// pages with LastEvaluatedKey carrying exactly the table's two key attributes plus the index's
    /// two; resuming from a page's token returns exactly the items the unpaged read still owes.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.gsi-query-last-evaluated-key-carries-every-table-and-index-key-attribute")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-gsilek-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("CustomerId", KeyType.HASH), new KeySchemaElement("OrderId", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("CustomerId", ScalarAttributeType.S),
                new AttributeDefinition("OrderId", ScalarAttributeType.S),
                new AttributeDefinition("Status", ScalarAttributeType.S),
                new AttributeDefinition("OrderSeq", ScalarAttributeType.N),
            ],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = Index,
                    KeySchema = [new KeySchemaElement("Status", KeyType.HASH), new KeySchemaElement("OrderSeq", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await WaitActive(ddb, table);

        for (var i = 0; i < NItems; i++)
        {
            await ddb.PutItemAsync(table, Item(
                ("CustomerId", S(Customers[i % Customers.Length])),
                ("OrderId", S("order-" + i.ToString("000", CultureInfo.InvariantCulture))),
                ("Status", S("PLACED")),
                ("OrderSeq", N(i.ToString(CultureInfo.InvariantCulture)))));
        }

        // The GSI is eventually consistent: poll the unpaged read until all 24 items are visible.
        var timeLeft = ctx.TimeLeftSeconds;
        var deadline = Math.Max(0.0, (timeLeft > 0 ? timeLeft : 300.0) - 60.0);
        var clock = FidelityTime.StartNew();
        QueryResponse unpaged;
        while (true)
        {
            unpaged = await QueryGroup(ddb, table);
            if (unpaged.Items.Count >= NItems)
            {
                break;
            }

            Ensure(clock.Elapsed.TotalSeconds < deadline, $"the index never held all {NItems} items; last read had {unpaged.Items.Count}");
            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        Ensure(unpaged.Items.Count == NItems, $"expected exactly {NItems} items from the unpaged GSI Query, got {unpaged.Items.Count}");
        var seqs = unpaged.Items.Select(i => int.Parse(i["OrderSeq"].N, CultureInfo.InvariantCulture)).ToList();
        Ensure(seqs.SequenceEqual(seqs.Order()), $"unpaged GSI Query was not in ascending OrderSeq order: {Py.Repr(seqs)}");
        Ensure(seqs.SequenceEqual(Enumerable.Range(0, NItems)), $"unpaged GSI Query returned OrderSeq values {Py.Repr(seqs)}");

        // Walk the same query with Limit, following LastEvaluatedKey.
        var paged = new List<Dictionary<string, AttributeValue>>();
        Dictionary<string, AttributeValue>? start = null;
        Dictionary<string, AttributeValue>? firstLek = null;
        var pages = 0;
        while (true)
        {
            var page = await QueryGroup(ddb, table, limit: Limit, start: start);
            var items = page.Items;
            Ensure(items.Count <= Limit, $"page {pages} held {items.Count} items, more than Limit={Limit}");
            paged.AddRange(items);
            pages++;
            Ensure(pages <= 20, "LastEvaluatedKey never ran out after 20 pages");
            start = page.LastEvaluatedKey;
            if (pages == 1)
            {
                firstLek = start;
            }

            if (start is null || start.Count == 0)
            {
                break;
            }

            Ensure(
                start.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(KeyAttributes),
                $"page {pages}'s LastEvaluatedKey carries {Py.Repr(start.Keys.Order(StringComparer.Ordinal).ToList())}, expected exactly the table's CustomerId+OrderId and the index's Status+OrderSeq");
            Ensure(
                Py.Equal(start, KeyOf(items[^1])),
                $"page {pages}'s LastEvaluatedKey {Py.Repr(start)} does not name its own last item {Py.Repr(KeyOf(items[^1]))}");
        }

        var expectedPages = (NItems + Limit - 1) / Limit;
        Ensure(pages == expectedPages, $"a Limit={Limit} walk of {NItems} items took {pages} pages, expected {expectedPages}");
        Ensure(
            Py.Equal(paged, unpaged.Items),
            $"the concatenated paged walk {Py.Repr(paged)} differs from the unpaged read {Py.Repr(unpaged.Items)}");

        // Resuming from the first page's token alone returns exactly the items still owed.
        Ensure(firstLek is not null, "expected a LastEvaluatedKey after the first Limit=5 page");
        var rest = await QueryGroup(ddb, table, start: firstLek);
        var owed = unpaged.Items.Skip(Limit).ToList();
        Ensure(
            Py.Equal(rest.Items, owed),
            $"resuming from the first page's LastEvaluatedKey returned {Py.Repr(rest.Items)}, expected exactly the {unpaged.Items.Count - Limit} remaining items {Py.Repr(owed)}");
        Ensure(
            rest.LastEvaluatedKey is null,
            $"resuming from the first page straight to the end still carried a LastEvaluatedKey: {Py.Repr(rest.LastEvaluatedKey)}");

        // The GSI's answer is backed by real table rows: GetItem on the first and last item by the
        // table's own primary key returns the same Status and OrderSeq the index reported.
        foreach (var item in new[] { unpaged.Items[0], unpaged.Items[^1] })
        {
            var got = (await ddb.GetItemAsync(new GetItemRequest
            {
                TableName = table,
                Key = Item(("CustomerId", item["CustomerId"]), ("OrderId", item["OrderId"])),
                ConsistentRead = true,
            })).Item;
            Ensure(
                Py.Equal(got["Status"], item["Status"]) && Py.Equal(got["OrderSeq"], item["OrderSeq"]),
                $"GetItem on {Py.Repr(item["CustomerId"])}/{Py.Repr(item["OrderId"])} returned Status={Py.Repr(got["Status"])} OrderSeq={Py.Repr(got["OrderSeq"])}, expected the GSI's own Status={Py.Repr(item["Status"])} OrderSeq={Py.Repr(item["OrderSeq"])}");
        }
    }
}

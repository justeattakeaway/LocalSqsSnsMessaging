using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DataModel;
using Amazon.DynamoDBv2.DocumentModel;
using Amazon.DynamoDBv2.Model;
using Shouldly;

namespace LocalSqsSnsMessaging.Tests.DynamoDb;

/// <summary>
/// Most .NET code reaches DynamoDB through <c>DynamoDBContext</c> (the object persistence model)
/// or the Document Model rather than the low-level client. Both describe the table first and send
/// some legacy parameters (<c>Expected</c> for versioned saves, <c>QueryFilter</c>), so these
/// exercise those paths end to end.
/// </summary>
public sealed class DynamoDbHighLevelApiTests
{
    [DynamoDBTable("Orders")]
    public sealed class Order
    {
        [DynamoDBHashKey]
        public string CustomerId { get; set; } = string.Empty;

        [DynamoDBRangeKey]
        public int OrderNumber { get; set; }

        [DynamoDBGlobalSecondaryIndexHashKey("ByStatus")]
        public string Status { get; set; } = string.Empty;

        public decimal Total { get; set; }

#pragma warning disable CA1002, CA2227 // DynamoDBContext maps a settable list property
        public List<string> Items { get; set; } = [];
#pragma warning restore CA1002, CA2227

        [DynamoDBVersion]
        public int? Version { get; set; }
    }

    private static async Task<AmazonDynamoDBClient> CreateOrdersTableAsync(InMemoryAwsBus bus)
    {
        var client = bus.CreateDynamoDbClient();
        await client.CreateTableAsync(new CreateTableRequest
        {
            TableName = "Orders",
            KeySchema = [new KeySchemaElement("CustomerId", KeyType.HASH), new KeySchemaElement("OrderNumber", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("CustomerId", ScalarAttributeType.S),
                new AttributeDefinition("OrderNumber", ScalarAttributeType.N),
                new AttributeDefinition("Status", ScalarAttributeType.S),
            ],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = "ByStatus",
                    KeySchema = [new KeySchemaElement("Status", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        return client;
    }

    private static DynamoDBContext CreateContext(IAmazonDynamoDB client) =>
        new DynamoDBContextBuilder().WithDynamoDBClient(() => client).Build();

    [Test]
    public async Task Context_SavesLoadsQueriesAndDeletes()
    {
        var bus = new InMemoryAwsBus();
        using var client = await CreateOrdersTableAsync(bus);
        using var context = CreateContext(client);

        await context.SaveAsync(new Order { CustomerId = "c1", OrderNumber = 1, Status = "OPEN", Total = 10.5m, Items = ["a", "b"] });
        await context.SaveAsync(new Order { CustomerId = "c1", OrderNumber = 2, Status = "SHIPPED", Total = 99.99m, Items = ["c"] });
        await context.SaveAsync(new Order { CustomerId = "c2", OrderNumber = 1, Status = "OPEN", Total = 5m });

        var loaded = await context.LoadAsync<Order>("c1", 2);
        loaded.Total.ShouldBe(99.99m);
        loaded.Items.ShouldBe(["c"]);
        loaded.Version.ShouldBe(0);

        var c1Orders = await context.QueryAsync<Order>("c1").GetRemainingAsync();
        c1Orders.Select(o => o.OrderNumber).ShouldBe([1, 2]);

        var recent = await context.QueryAsync<Order>("c1", QueryOperator.GreaterThan, [1]).GetRemainingAsync();
        recent.Single().OrderNumber.ShouldBe(2);

        var open = await context.QueryAsync<Order>("OPEN", new QueryConfig { IndexName = "ByStatus" }).GetRemainingAsync();
        open.Select(o => o.CustomerId).OrderBy(c => c, StringComparer.Ordinal).ShouldBe(["c1", "c2"]);

        var expensive = await context.ScanAsync<Order>([new ScanCondition("Total", ScanOperator.GreaterThan, 50m)]).GetRemainingAsync();
        expensive.Single().OrderNumber.ShouldBe(2);

        await context.DeleteAsync<Order>("c2", 1);
        (await context.LoadAsync<Order>("c2", 1)).ShouldBeNull();
    }

    [Test]
    public async Task Context_VersionedSave_RefusesAStaleCopy()
    {
        var bus = new InMemoryAwsBus();
        using var client = await CreateOrdersTableAsync(bus);
        using var context = CreateContext(client);
        await context.SaveAsync(new Order { CustomerId = "c1", OrderNumber = 1, Status = "OPEN" });

        var first = await context.LoadAsync<Order>("c1", 1);
        var stale = await context.LoadAsync<Order>("c1", 1);
        first.Status = "SHIPPED";
        await context.SaveAsync(first);

        stale.Status = "CANCELLED";
        await Should.ThrowAsync<ConditionalCheckFailedException>(() => context.SaveAsync(stale));
        (await context.LoadAsync<Order>("c1", 1)).Status.ShouldBe("SHIPPED");
    }

    [Test]
    public async Task Context_BatchAndTransactionalWrites()
    {
        var bus = new InMemoryAwsBus();
        using var client = await CreateOrdersTableAsync(bus);
        using var context = CreateContext(client);

        // The SDK refuses a batch write of a versioned type unless version checks are skipped.
        var batch = context.CreateBatchWrite<Order>(new BatchWriteConfig { SkipVersionCheck = true });
        batch.AddPutItems(Enumerable.Range(1, 30).Select(i => new Order { CustomerId = "bulk", OrderNumber = i, Status = "OPEN" }));
        await batch.ExecuteAsync();

        var get = context.CreateBatchGet<Order>();
        get.AddKey("bulk", 3);
        get.AddKey("bulk", 30);
        await get.ExecuteAsync();
        get.Results.Select(o => o.OrderNumber).OrderBy(n => n).ShouldBe([3, 30]);

        var transaction = context.CreateTransactWrite<Order>();
        transaction.AddSaveItem(new Order { CustomerId = "tx", OrderNumber = 1, Status = "OPEN" });
        transaction.AddDeleteKey("bulk", 1);
        await transaction.ExecuteAsync();

        (await context.LoadAsync<Order>("tx", 1)).ShouldNotBeNull();
        (await context.LoadAsync<Order>("bulk", 1)).ShouldBeNull();
    }

    [Test]
    public async Task DocumentModel_PutGetUpdateAndFilteredQuery()
    {
        var bus = new InMemoryAwsBus();
        using var client = await CreateOrdersTableAsync(bus);
        var table = new TableBuilder(client, "Orders")
            .AddHashKey("CustomerId", DynamoDBEntryType.String)
            .AddRangeKey("OrderNumber", DynamoDBEntryType.Numeric)
            .Build();

        await table.PutItemAsync(new Document { ["CustomerId"] = "c1", ["OrderNumber"] = 1, ["Status"] = "OPEN", ["Total"] = 3 });
        await table.PutItemAsync(new Document { ["CustomerId"] = "c1", ["OrderNumber"] = 2, ["Status"] = "OPEN", ["Total"] = 30 });

        var update = new Document { ["CustomerId"] = "c1", ["OrderNumber"] = 1, ["Status"] = "SHIPPED" };
        await table.UpdateItemAsync(update, new UpdateItemOperationConfig
        {
            ExpectedState = new ExpectedState { ExpectedValues = { ["Status"] = new ExpectedValue(ScanOperator.Equal, "OPEN") } },
        });
        (await table.GetItemAsync("c1", 1))["Status"].AsString().ShouldBe("SHIPPED");

        var filter = new QueryFilter("CustomerId", QueryOperator.Equal, "c1");
        filter.AddCondition("Total", ScanOperator.GreaterThan, 10);
        var results = await table.Query(filter).GetRemainingAsync();
        results.Single()["OrderNumber"].AsInt().ShouldBe(2);
    }
}

using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace LocalSqsSnsMessaging.Tests.DynamoDb;

/// <summary>DynamoDB through a real <see cref="AmazonDynamoDBClient"/> over the in-memory handler.</summary>
public sealed class DynamoDbClientTests
{
    private static async Task<AmazonDynamoDBClient> CreateTableAsync(InMemoryAwsBus bus, string name = "orders")
    {
        var client = bus.CreateDynamoDbClient();
        await client.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.N)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        return client;
    }

    [Test]
    public async Task ListTables_ReturnsCreatedTables()
    {
        var bus = new InMemoryAwsBus();
        var client = await CreateTableAsync(bus);

        var tables = await client.ListTablesAsync(new ListTablesRequest());

        tables.TableNames.ShouldBe(["orders"]);
    }

    [Test]
    public async Task PutThenGet_RoundTripsEveryAttributeType()
    {
        var bus = new InMemoryAwsBus();
        var client = await CreateTableAsync(bus);
        var item = new Dictionary<string, AttributeValue>
        {
            ["pk"] = new("a"),
            ["sk"] = new() { N = "1.50" },
            ["s"] = new("text"),
            ["b"] = new() { B = new MemoryStream([1, 2, 3]) },
            ["ss"] = new() { SS = ["x", "y"] },
            ["ns"] = new() { NS = ["1", "2"] },
            ["bool"] = new() { BOOL = true },
            ["null"] = new() { NULL = true },
            ["l"] = new() { L = [new("one"), new() { N = "2" }, new() { L = [] }] },
            ["m"] = new() { M = new Dictionary<string, AttributeValue> { ["nested"] = new() { M = new Dictionary<string, AttributeValue> { ["deep"] = new("v") } } } },
        };

        await client.PutItemAsync("orders", item);
        var got = (await client.GetItemAsync("orders", new Dictionary<string, AttributeValue> { ["pk"] = new("a"), ["sk"] = new() { N = "1.5" } })).Item;

        got["sk"].N.ShouldBe("1.5");
        got["s"].S.ShouldBe("text");
        got["b"].B.ToArray().ShouldBe([1, 2, 3]);
        got["ss"].SS.ShouldBe(["x", "y"]);
        got["bool"].BOOL.ShouldBe(true);
        got["null"].NULL.ShouldBe(true);
        got["l"].L.Count.ShouldBe(3);
        got["l"].L[2].IsLSet.ShouldBeTrue();
        got["m"].M["nested"].M["deep"].S.ShouldBe("v");
    }

    [Test]
    public async Task DeeplyNestedValue_RoundTrips()
    {
        var bus = new InMemoryAwsBus();
        var client = await CreateTableAsync(bus);
        var value = new AttributeValue("leaf");
        for (var i = 0; i < 30; i++)
        {
            value = new AttributeValue { M = new Dictionary<string, AttributeValue> { ["k"] = value } };
        }

        await client.PutItemAsync("orders", new Dictionary<string, AttributeValue> { ["pk"] = new("a"), ["sk"] = new() { N = "1" }, ["deep"] = value });
        var got = (await client.GetItemAsync("orders", new Dictionary<string, AttributeValue> { ["pk"] = new("a"), ["sk"] = new() { N = "1" } })).Item["deep"];
        for (var i = 0; i < 30; i++)
        {
            got = got.M["k"];
        }

        got.S.ShouldBe("leaf");
    }

    [Test]
    public async Task UpdateItem_AppliesExpressions()
    {
        var bus = new InMemoryAwsBus();
        var client = await CreateTableAsync(bus);
        var key = new Dictionary<string, AttributeValue> { ["pk"] = new("a"), ["sk"] = new() { N = "1" } };

        var response = await client.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = "orders",
            Key = key,
            UpdateExpression = "SET #c = if_not_exists(#c, :zero) + :one, tags = list_append(if_not_exists(tags, :empty), :tag) ADD seen :set",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#c"] = "count" },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":zero"] = new() { N = "0" },
                [":one"] = new() { N = "1" },
                [":empty"] = new() { L = [] },
                [":tag"] = new() { L = [new("t1")] },
                [":set"] = new() { SS = ["s1"] },
            },
            ReturnValues = ReturnValue.ALL_NEW,
        });

        response.Attributes["count"].N.ShouldBe("1");
        response.Attributes["tags"].L.Select(v => v.S).ShouldBe(["t1"]);
        response.Attributes["seen"].SS.ShouldBe(["s1"]);
    }

    [Test]
    public async Task ConditionalPut_FailsWithConditionalCheckFailed()
    {
        var bus = new InMemoryAwsBus();
        var client = await CreateTableAsync(bus);
        var item = new Dictionary<string, AttributeValue> { ["pk"] = new("a"), ["sk"] = new() { N = "1" } };
        await client.PutItemAsync("orders", item);

        var error = await Should.ThrowAsync<ConditionalCheckFailedException>(() => client.PutItemAsync(new PutItemRequest
        {
            TableName = "orders",
            Item = item,
            ConditionExpression = "attribute_not_exists(pk)",
        }));

        error.Message.ShouldBe("The conditional request failed");
    }

    [Test]
    public async Task Query_PagesWithLimitAndRespectsSortOrder()
    {
        var bus = new InMemoryAwsBus();
        var client = await CreateTableAsync(bus);
        for (var i = 1; i <= 5; i++)
        {
            await client.PutItemAsync("orders", new Dictionary<string, AttributeValue> { ["pk"] = new("a"), ["sk"] = new() { N = i.ToString(CultureInfo.InvariantCulture) } });
        }

        var first = await client.QueryAsync(new QueryRequest
        {
            TableName = "orders",
            KeyConditionExpression = "pk = :p AND sk > :lo",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":p"] = new("a"), [":lo"] = new() { N = "1" } },
            ScanIndexForward = false,
            Limit = 2,
        });

        first.Items.Select(i => i["sk"].N).ShouldBe(["5", "4"]);
        first.LastEvaluatedKey["sk"].N.ShouldBe("4");
    }

    [Test]
    public async Task UnknownTable_IsResourceNotFound()
    {
        var bus = new InMemoryAwsBus();
        var client = bus.CreateDynamoDbClient();

        await Should.ThrowAsync<ResourceNotFoundException>(() => client.DescribeTableAsync("missing"));
    }

    [Test]
    public async Task ValidationError_UsesTheEnvelope()
    {
        var bus = new InMemoryAwsBus();
        var client = await CreateTableAsync(bus);

        var error = await Should.ThrowAsync<AmazonDynamoDBException>(() => client.PutItemAsync(new PutItemRequest
        {
            TableName = "orders",
            Item = new Dictionary<string, AttributeValue> { ["pk"] = new("a"), ["sk"] = new() { N = "1" } },
            ReturnConsumedCapacity = "BOGUS",
        }));

        error.ErrorCode.ShouldBe("ValidationException");
        error.Message.ShouldBe("1 validation error detected: Value 'BOGUS' at 'returnConsumedCapacity' failed to satisfy constraint: Member must satisfy enum value set: [INDEXES, TOTAL, NONE]");
    }

    [Test]
    public async Task TransitionDelay_KeepsANewTableCreatingUntilItPasses()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var bus = new InMemoryAwsBus { TimeProvider = time, DynamoDbTransitionDelay = TimeSpan.FromSeconds(5) };
        var client = bus.CreateDynamoDbClient();

        var created = await client.CreateTableAsync(new CreateTableRequest
        {
            TableName = "orders",
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        var item = new Dictionary<string, AttributeValue> { ["pk"] = new("a") };

        created.TableDescription.TableStatus.ShouldBe(TableStatus.CREATING);
        await Should.ThrowAsync<ResourceNotFoundException>(() => client.PutItemAsync("orders", item));

        time.Advance(TimeSpan.FromSeconds(5));

        (await client.DescribeTableAsync("orders")).Table.TableStatus.ShouldBe(TableStatus.ACTIVE);
        await client.PutItemAsync("orders", item);
    }

    [Test]
    public async Task TransitionDelay_KeepsADeletedTableDeletingUntilItIsGone()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var bus = new InMemoryAwsBus { TimeProvider = time, DynamoDbTransitionDelay = TimeSpan.FromSeconds(5) };
        var client = await CreateTableAsync(bus);
        time.Advance(TimeSpan.FromSeconds(5));

        (await client.DeleteTableAsync("orders")).TableDescription.TableStatus.ShouldBe(TableStatus.DELETING);
        (await client.DescribeTableAsync("orders")).Table.TableStatus.ShouldBe(TableStatus.DELETING);
        var again = await Should.ThrowAsync<ResourceInUseException>(() => client.DeleteTableAsync("orders"));
        again.Message.ShouldBe("Attempt to change a resource which is still in use: Table is being deleted: orders");

        time.Advance(TimeSpan.FromSeconds(5));

        await Should.ThrowAsync<ResourceNotFoundException>(() => client.DescribeTableAsync("orders"));
        (await client.ListTablesAsync(new ListTablesRequest())).TableNames.ShouldBeEmpty();
    }

    [Test]
    public async Task TransitionDelay_KeepsATableUpdatingWhileItsThroughputChanges()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var bus = new InMemoryAwsBus { TimeProvider = time, DynamoDbTransitionDelay = TimeSpan.FromSeconds(5) };
        var client = await CreateTableAsync(bus);
        time.Advance(TimeSpan.FromSeconds(5));

        var updated = await client.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = "orders",
            BillingMode = BillingMode.PROVISIONED,
            ProvisionedThroughput = new ProvisionedThroughput(3, 3),
        });

        updated.TableDescription.TableStatus.ShouldBe(TableStatus.UPDATING);
        var refused = await Should.ThrowAsync<ResourceInUseException>(() => client.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = "orders",
            ProvisionedThroughput = new ProvisionedThroughput(5, 5),
        }));
        refused.Message.ShouldBe("Attempt to change a resource which is still in use: Table IOPS are currently being updated. Table: orders");
        await client.PutItemAsync("orders", new Dictionary<string, AttributeValue> { ["pk"] = new("a"), ["sk"] = new() { N = "1" } });

        time.Advance(TimeSpan.FromSeconds(5));

        (await client.DescribeTableAsync("orders")).Table.TableStatus.ShouldBe(TableStatus.ACTIVE);
    }

    [Test]
    public async Task TransitionDelay_BuildsAnIndexUpdateTableAddsOneAtATime()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var bus = new InMemoryAwsBus { TimeProvider = time, DynamoDbTransitionDelay = TimeSpan.FromSeconds(5) };
        var client = await CreateTableAsync(bus);
        time.Advance(TimeSpan.FromSeconds(5));

        UpdateTableRequest AddIndex(string name) => new()
        {
            TableName = "orders",
            AttributeDefinitions = [new AttributeDefinition(name, ScalarAttributeType.S)],
            GlobalSecondaryIndexUpdates =
            [
                new GlobalSecondaryIndexUpdate
                {
                    Create = new CreateGlobalSecondaryIndexAction
                    {
                        IndexName = name,
                        KeySchema = [new KeySchemaElement(name, KeyType.HASH)],
                        Projection = new Projection { ProjectionType = ProjectionType.ALL },
                    },
                },
            ],
        };

        var added = await client.UpdateTableAsync(AddIndex("by-a"));

        added.TableDescription.TableStatus.ShouldBe(TableStatus.UPDATING);
        added.TableDescription.GlobalSecondaryIndexes.Single().IndexStatus.ShouldBe(IndexStatus.CREATING);
        await Should.ThrowAsync<LimitExceededException>(() => client.UpdateTableAsync(AddIndex("by-b")));
        var blocked = await Should.ThrowAsync<ResourceInUseException>(() => client.DeleteTableAsync("orders"));
        blocked.Message.ShouldBe("Attempt to change a resource which is still in use: Cannot delete table while indexes are being created, updated, or deleted.");

        time.Advance(TimeSpan.FromSeconds(5));

        var settled = (await client.DescribeTableAsync("orders")).Table;
        settled.TableStatus.ShouldBe(TableStatus.ACTIVE);
        settled.GlobalSecondaryIndexes.Single().IndexStatus.ShouldBe(IndexStatus.ACTIVE);
        await client.UpdateTableAsync(AddIndex("by-b"));
    }
}

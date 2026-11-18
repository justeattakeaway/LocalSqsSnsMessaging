using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace InMemoryAws.Tests.DynamoDb;

/// <summary>DynamoDB through a real <see cref="AmazonDynamoDBClient"/> over the in-memory handler.</summary>
public sealed class DynamoDbClientTests
{
    private static async Task<AmazonDynamoDBClient> CreateTableAsync(InMemoryAwsCloud cloud, string name = "orders")
    {
        var client = cloud.CreateDynamoDbClient();
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
        var cloud = new InMemoryAwsCloud();
        using var client = await CreateTableAsync(cloud);

        var tables = await client.ListTablesAsync(new ListTablesRequest());

        tables.TableNames.ShouldBe(["orders"]);
    }

    [Test]
    public async Task PutThenGet_RoundTripsEveryAttributeType()
    {
        var cloud = new InMemoryAwsCloud();
        using var client = await CreateTableAsync(cloud);
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
        var cloud = new InMemoryAwsCloud();
        using var client = await CreateTableAsync(cloud);
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
        var cloud = new InMemoryAwsCloud();
        using var client = await CreateTableAsync(cloud);
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
        var cloud = new InMemoryAwsCloud();
        using var client = await CreateTableAsync(cloud);
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
        var cloud = new InMemoryAwsCloud();
        using var client = await CreateTableAsync(cloud);
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
        var cloud = new InMemoryAwsCloud();
        using var client = cloud.CreateDynamoDbClient();

        await Should.ThrowAsync<ResourceNotFoundException>(() => client.DescribeTableAsync("missing"));
    }

    [Test]
    public async Task ValidationError_UsesTheEnvelope()
    {
        var cloud = new InMemoryAwsCloud();
        using var client = await CreateTableAsync(cloud);

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
        var cloud = new InMemoryAwsCloud { TimeProvider = time, DynamoDbTransitionDelay = TimeSpan.FromSeconds(5) };
        using var client = cloud.CreateDynamoDbClient();

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
        var cloud = new InMemoryAwsCloud { TimeProvider = time, DynamoDbTransitionDelay = TimeSpan.FromSeconds(5) };
        using var client = await CreateTableAsync(cloud);
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
        var cloud = new InMemoryAwsCloud { TimeProvider = time, DynamoDbTransitionDelay = TimeSpan.FromSeconds(5) };
        using var client = await CreateTableAsync(cloud);
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
    public async Task TransitionDelay_KeepsATableUpdatingWhileItSwitchesToOnDemand()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var cloud = new InMemoryAwsCloud { TimeProvider = time, DynamoDbTransitionDelay = TimeSpan.FromSeconds(5) };
        using var client = await CreateTableAsync(cloud);
        time.Advance(TimeSpan.FromSeconds(5));
        await client.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = "orders",
            BillingMode = BillingMode.PROVISIONED,
            ProvisionedThroughput = new ProvisionedThroughput(3, 3),
        });
        time.Advance(TimeSpan.FromSeconds(5));

        var updated = await client.UpdateTableAsync(new UpdateTableRequest { TableName = "orders", BillingMode = BillingMode.PAY_PER_REQUEST });

        updated.TableDescription.TableStatus.ShouldBe(TableStatus.UPDATING);
        var refused = await Should.ThrowAsync<ResourceInUseException>(() => client.DeleteTableAsync("orders"));
        refused.Message.ShouldBe("Attempt to change a resource which is still in use: Table is being updated: orders");

        time.Advance(TimeSpan.FromSeconds(5));

        (await client.DescribeTableAsync("orders")).Table.TableStatus.ShouldBe(TableStatus.ACTIVE);
    }

    [Test]
    public async Task TransitionDelay_BuildsAnIndexUpdateTableAddsOneAtATime()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var cloud = new InMemoryAwsCloud { TimeProvider = time, DynamoDbTransitionDelay = TimeSpan.FromSeconds(5) };
        using var client = await CreateTableAsync(cloud);
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

    [Test]
    public async Task MultiAttributeIndexKey_QueriesInSchemaOrderAndPagesByEveryKeyAttribute()
    {
        var cloud = new InMemoryAwsCloud();
        using var client = cloud.CreateDynamoDbClient();
        await client.CreateTableAsync(new CreateTableRequest
        {
            TableName = "events",
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("tenant", ScalarAttributeType.S),
                new AttributeDefinition("region", ScalarAttributeType.S),
                new AttributeDefinition("day", ScalarAttributeType.N),
                new AttributeDefinition("seq", ScalarAttributeType.N),
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = "by-tenant-region",
                    KeySchema =
                    [
                        new KeySchemaElement("tenant", KeyType.HASH),
                        new KeySchemaElement("region", KeyType.HASH),
                        new KeySchemaElement("day", KeyType.RANGE),
                        new KeySchemaElement("seq", KeyType.RANGE),
                    ],
                    Projection = new Projection { ProjectionType = ProjectionType.KEYS_ONLY },
                },
            ],
        });
        foreach (var (pk, region, day, seq) in new[] { ("a", "eu", "2", "1"), ("b", "eu", "1", "10"), ("c", "eu", "1", "9"), ("d", "us", "1", "1") })
        {
            await client.PutItemAsync("events", new Dictionary<string, AttributeValue>
            {
                ["pk"] = new(pk),
                ["tenant"] = new("acme"),
                ["region"] = new(region),
                ["day"] = new() { N = day },
                ["seq"] = new() { N = seq },
            });
        }

        var pks = new List<string>();
        Dictionary<string, AttributeValue>? start = null;
        do
        {
            var page = await client.QueryAsync(new QueryRequest
            {
                TableName = "events",
                IndexName = "by-tenant-region",
                KeyConditionExpression = "#r = :r AND tenant = :t",
                ExpressionAttributeNames = new Dictionary<string, string> { ["#r"] = "region" },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":t"] = new("acme"), [":r"] = new("eu") },
                Limit = 1,
                ExclusiveStartKey = start,
            });
            pks.AddRange(page.Items.Select(i => i["pk"].S));
            start = page.LastEvaluatedKey;
            if (start is { Count: > 0 })
            {
                start.Keys.Order(StringComparer.Ordinal).ShouldBe(["day", "pk", "region", "seq", "tenant"]);
            }
        }
        while (start is { Count: > 0 });

        // Ordered by day, then numerically by seq.
        pks.ShouldBe(["c", "b", "a"]);
    }

    [Test]
    public async Task ReplicaUpdates_MakeAGlobalTableWhoseWritesReachEveryRegion()
    {
        var cloud = new InMemoryAwsCloud { CurrentRegion = "us-east-1" };
        using var east = await CreateTableAsync(cloud);
        using var west = new AmazonDynamoDBClient(
            new Amazon.Runtime.BasicAWSCredentials("test", "test"),
            new AmazonDynamoDBConfig
            {
                RegionEndpoint = Amazon.RegionEndpoint.USWest2,
                HttpClientFactory = new InMemoryAwsSdkHttpClientFactory(cloud),
            });
        await east.PutItemAsync("orders", new Dictionary<string, AttributeValue> { ["pk"] = new("before"), ["sk"] = new() { N = "1" } });

        await east.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = "orders",
            ReplicaUpdates = [new ReplicationGroupUpdate { Create = new CreateReplicationGroupMemberAction { RegionName = "us-west-2" } }],
        });

        var described = (await east.DescribeTableAsync("orders")).Table;
        described.GlobalTableVersion.ShouldBe("2019.11.21");
        described.Replicas.Select(r => (r.RegionName, r.ReplicaStatus.Value)).ShouldBe([("us-west-2", "ACTIVE")]);
        (await west.DescribeTableAsync("orders")).Table.Replicas.Single().RegionName.ShouldBe("us-east-1");

        // The items the table had are copied, and each region's writes reach the other.
        var key = new Dictionary<string, AttributeValue> { ["pk"] = new("before"), ["sk"] = new() { N = "1" } };
        (await west.GetItemAsync("orders", key)).Item.ShouldNotBeEmpty();
        await west.PutItemAsync("orders", new Dictionary<string, AttributeValue> { ["pk"] = new("west"), ["sk"] = new() { N = "2" } });
        (await east.GetItemAsync("orders", new Dictionary<string, AttributeValue> { ["pk"] = new("west"), ["sk"] = new() { N = "2" } })).Item.ShouldNotBeEmpty();
        await east.DeleteItemAsync("orders", key);
        (await west.GetItemAsync("orders", key)).Item.ShouldBeNull();

        // The region a replica was added from can't be deleted for a day; removing the replica frees it.
        var refused = await Should.ThrowAsync<AmazonDynamoDBException>(() => east.DeleteTableAsync("orders"));
        refused.ErrorCode.ShouldBe("ValidationException");
        await east.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = "orders",
            ReplicaUpdates = [new ReplicationGroupUpdate { Delete = new DeleteReplicationGroupMemberAction { RegionName = "us-west-2" } }],
        });
        (await east.DescribeTableAsync("orders")).Table.Replicas.ShouldBeNull();
        await Should.ThrowAsync<ResourceNotFoundException>(() => west.DescribeTableAsync("orders"));
        await east.DeleteTableAsync("orders");
    }

    private static AmazonDynamoDBClient CreateWestClient(InMemoryAwsCloud cloud) => new(
        new Amazon.Runtime.BasicAWSCredentials("test", "test"),
        new AmazonDynamoDBConfig
        {
            RegionEndpoint = Amazon.RegionEndpoint.USWest2,
            HttpClientFactory = new InMemoryAwsSdkHttpClientFactory(cloud),
        });

    private static Task<UpdateTableResponse> AddWestReplicaAsync(AmazonDynamoDBClient client) => client.UpdateTableAsync(new UpdateTableRequest
    {
        TableName = "orders",
        ReplicaUpdates = [new ReplicationGroupUpdate { Create = new CreateReplicationGroupMemberAction { RegionName = "us-west-2" } }],
    });

    [Test]
    public async Task ReplicaUpdates_KeepIndexesCapacityStreamAndTimeToLiveTheSameInEveryRegion()
    {
        var cloud = new InMemoryAwsCloud { CurrentRegion = "us-east-1" };
        using var east = await CreateTableAsync(cloud);
        using var west = CreateWestClient(cloud);
        await AddWestReplicaAsync(east);

        // An index one region adds is built in every region, and queryable there.
        await east.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = "orders",
            AttributeDefinitions = [new AttributeDefinition("status", ScalarAttributeType.S)],
            GlobalSecondaryIndexUpdates =
            [
                new GlobalSecondaryIndexUpdate
                {
                    Create = new CreateGlobalSecondaryIndexAction
                    {
                        IndexName = "by-status",
                        KeySchema = [new KeySchemaElement("status", KeyType.HASH)],
                        Projection = new Projection { ProjectionType = ProjectionType.ALL },
                    },
                },
            ],
        });
        await east.PutItemAsync("orders", new Dictionary<string, AttributeValue> { ["pk"] = new("a"), ["sk"] = new() { N = "1" }, ["status"] = new("open") });
        var found = await west.QueryAsync(new QueryRequest
        {
            TableName = "orders",
            IndexName = "by-status",
            KeyConditionExpression = "#s = :s",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#s"] = "status" },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":s"] = new("open") },
        });
        found.Items.Select(i => i["pk"].S).ShouldBe(["a"]);

        // So do a stream and a change of capacity, from whichever region makes them.
        await east.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = "orders",
            StreamSpecification = new StreamSpecification { StreamEnabled = true, StreamViewType = StreamViewType.NEW_AND_OLD_IMAGES },
        });
        await west.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = "orders",
            BillingMode = BillingMode.PROVISIONED,
            ProvisionedThroughput = new ProvisionedThroughput(5, 5),
            GlobalSecondaryIndexUpdates =
            [
                new GlobalSecondaryIndexUpdate
                {
                    Update = new UpdateGlobalSecondaryIndexAction { IndexName = "by-status", ProvisionedThroughput = new ProvisionedThroughput(5, 5) },
                },
            ],
        });
        var westTable = (await west.DescribeTableAsync("orders")).Table;
        westTable.StreamSpecification.StreamEnabled.ShouldBe(true);
        westTable.LatestStreamArn.ShouldStartWith("arn:aws:dynamodb:us-west-2:");
        var eastTable = (await east.DescribeTableAsync("orders")).Table;
        eastTable.BillingModeSummary.BillingMode.Value.ShouldBe("PROVISIONED");
        eastTable.ProvisionedThroughput.WriteCapacityUnits.ShouldBe(5);
        eastTable.GlobalSecondaryIndexes.Single().ProvisionedThroughput.WriteCapacityUnits.ShouldBe(5);

        // Time to live, too.
        await west.UpdateTimeToLiveAsync(new UpdateTimeToLiveRequest
        {
            TableName = "orders",
            TimeToLiveSpecification = new TimeToLiveSpecification { Enabled = true, AttributeName = "expiresAt" },
        });
        var eastTimeToLive = (await east.DescribeTimeToLiveAsync(new DescribeTimeToLiveRequest { TableName = "orders" })).TimeToLiveDescription;
        eastTimeToLive.TimeToLiveStatus.Value.ShouldBe("ENABLED");
        eastTimeToLive.AttributeName.ShouldBe("expiresAt");
    }

    [Test]
    public async Task ReplicaUpdates_GiveANewReplicaTheTablesTimeToLive_WhichOutlivesTheSourceRegion()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(start);
        var cloud = new InMemoryAwsCloud { CurrentRegion = "us-east-1", TimeProvider = time };
        using var east = await CreateTableAsync(cloud);
        using var west = CreateWestClient(cloud);
        await east.UpdateTimeToLiveAsync(new UpdateTimeToLiveRequest
        {
            TableName = "orders",
            TimeToLiveSpecification = new TimeToLiveSpecification { Enabled = true, AttributeName = "expiresAt" },
        });

        await AddWestReplicaAsync(east);

        var westTimeToLive = (await west.DescribeTimeToLiveAsync(new DescribeTimeToLiveRequest { TableName = "orders" })).TimeToLiveDescription;
        westTimeToLive.TimeToLiveStatus.Value.ShouldBe("ENABLED");
        westTimeToLive.AttributeName.ShouldBe("expiresAt");

        // Once the source region's table is gone, the replica still expires its items itself.
        time.Advance(TimeSpan.FromHours(25));
        await east.DeleteTableAsync("orders");
        var expiresAt = time.GetUtcNow().AddMinutes(5).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var key = new Dictionary<string, AttributeValue> { ["pk"] = new("a"), ["sk"] = new() { N = "1" } };
        await west.PutItemAsync("orders", new Dictionary<string, AttributeValue>(key) { ["expiresAt"] = new() { N = expiresAt } });

        time.Advance(TimeSpan.FromMinutes(10));

        (await west.GetItemAsync("orders", key)).Item.ShouldBeNull();
    }
}

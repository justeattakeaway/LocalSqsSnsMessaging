using AmazonDynamoDBStreamsClient = Amazon.DynamoDBStreams.AmazonDynamoDBStreamsClient;
using AmazonDynamoDBStreamsConfig = Amazon.DynamoDBStreams.AmazonDynamoDBStreamsConfig;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Streams = Amazon.DynamoDBStreams.Model;

namespace LocalSqsSnsMessaging.Tests.DynamoDb;

/// <summary>Regression tests for DynamoDB behaviour the evaluation suite doesn't cover.</summary>
public sealed class DynamoDbRegressionTests
{
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private static async Task<AmazonDynamoDBClient> CreateTableAsync(
        InMemoryAwsBus bus,
        string name = "tbl",
        StreamViewType? viewType = null,
        BillingMode? billingMode = null)
    {
        var client = bus.CreateDynamoDbClient();
        var provisioned = billingMode == BillingMode.PROVISIONED;
        await client.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.N)],
            BillingMode = billingMode ?? BillingMode.PAY_PER_REQUEST,
            ProvisionedThroughput = provisioned ? new ProvisionedThroughput(5, 5) : null,
            StreamSpecification = viewType is null ? null : new StreamSpecification { StreamEnabled = true, StreamViewType = viewType },
        });
        return client;
    }

    private static Dictionary<string, AttributeValue> Item(string pk, int sk, params (string Name, string Value)[] attributes)
    {
        var item = new Dictionary<string, AttributeValue> { ["pk"] = new(pk), ["sk"] = new() { N = sk.ToString(CultureInfo.InvariantCulture) } };
        foreach (var (name, value) in attributes)
        {
            item[name] = new AttributeValue(value);
        }

        return item;
    }

    private static AmazonDynamoDBStreamsClient CreateStreamsClient(InMemoryAwsBus bus) =>
        new(new AnonymousAWSCredentials(), new AmazonDynamoDBStreamsConfig
        {
            ServiceURL = $"https://streams.dynamodb.{bus.CurrentRegion}.amazonaws.com",
            AuthenticationRegion = bus.CurrentRegion,
            MaxErrorRetry = 0,
            HttpClientFactory = bus.CreateAwsHttpClientFactory(),
        });

    private static async Task<List<Streams.Record>> ReadAllAsync(AmazonDynamoDBStreamsClient streams, string streamArn)
    {
        var description = (await streams.DescribeStreamAsync(new Streams.DescribeStreamRequest { StreamArn = streamArn })).StreamDescription;
        var iterator = (await streams.GetShardIteratorAsync(new Streams.GetShardIteratorRequest
        {
            StreamArn = streamArn,
            ShardId = description.Shards[0].ShardId,
            ShardIteratorType = Amazon.DynamoDBStreams.ShardIteratorType.TRIM_HORIZON,
        })).ShardIterator;
        return (await streams.GetRecordsAsync(new Streams.GetRecordsRequest { ShardIterator = iterator })).Records;
    }

    [Test]
    public async Task DeleteTable_StopsTheTimeToLiveSweep()
    {
        var time = new TimerCountingTimeProvider(new FakeTimeProvider(Start));
        var bus = new InMemoryAwsBus { TimeProvider = time };
        using var client = await CreateTableAsync(bus);
        await client.UpdateTimeToLiveAsync(new UpdateTimeToLiveRequest
        {
            TableName = "tbl",
            TimeToLiveSpecification = new TimeToLiveSpecification { Enabled = true, AttributeName = "expiresAt" },
        });
        time.LiveTimers.ShouldBe(1);

        await client.DeleteTableAsync("tbl");

        time.LiveTimers.ShouldBe(0);
    }

    [Test]
    public async Task Stream_WritesNoRecordForAWriteThatChangesNothing()
    {
        var bus = new InMemoryAwsBus();
        using var client = await CreateTableAsync(bus, viewType: StreamViewType.NEW_AND_OLD_IMAGES);
        using var streams = CreateStreamsClient(bus);

        await client.PutItemAsync("tbl", Item("a", 1, ("v", "1")));
        await client.PutItemAsync("tbl", Item("a", 1, ("v", "1")));
        await client.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = "tbl",
            Key = Item("a", 1),
            UpdateExpression = "SET v = :v REMOVE absent",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":v"] = new("1") },
        });
        await client.PutItemAsync("tbl", Item("a", 1, ("v", "2")));

        var streamArn = (await client.DescribeTableAsync("tbl")).Table.LatestStreamArn;
        var records = await ReadAllAsync(streams, streamArn);

        records.Select(r => r.EventName.Value).ShouldBe(["INSERT", "MODIFY"]);
        records[1].Dynamodb.NewImage["v"].S.ShouldBe("2");
    }

    [Test]
    public async Task Stream_OfARecreatedTableDoesNotReplaceTheOldStream()
    {
        // The clock never moves, so both streams are made at the same instant.
        var bus = new InMemoryAwsBus { TimeProvider = new FakeTimeProvider(Start) };
        using var client = await CreateTableAsync(bus, viewType: StreamViewType.NEW_IMAGE);
        using var streams = CreateStreamsClient(bus);
        await client.PutItemAsync("tbl", Item("old", 1));
        var oldStreamArn = (await client.DescribeTableAsync("tbl")).Table.LatestStreamArn;

        await client.DeleteTableAsync("tbl");
        using var recreated = await CreateTableAsync(bus, viewType: StreamViewType.NEW_IMAGE);
        await recreated.PutItemAsync("tbl", Item("new", 1));
        var newStreamArn = (await recreated.DescribeTableAsync("tbl")).Table.LatestStreamArn;

        newStreamArn.ShouldNotBe(oldStreamArn);
        (await streams.ListStreamsAsync(new Streams.ListStreamsRequest { TableName = "tbl" })).Streams.Count.ShouldBe(2);
        (await ReadAllAsync(streams, oldStreamArn)).Single().Dynamodb.Keys["pk"].S.ShouldBe("old");
        (await ReadAllAsync(streams, newStreamArn)).Single().Dynamodb.Keys["pk"].S.ShouldBe("new");
    }

    [Test]
    public async Task ExecuteStatement_DescendingPagesResumeAfterTheLastItemIsDeleted()
    {
        var bus = new InMemoryAwsBus();
        using var client = await CreateTableAsync(bus);
        for (var sk = 1; sk <= 5; sk++)
        {
            await client.PutItemAsync("tbl", Item("A", sk));
        }

        const string Statement = "SELECT * FROM \"tbl\" WHERE pk = 'A' ORDER BY sk DESC";
        var seen = new List<string>();
        string? token = null;
        do
        {
            var page = await client.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = Statement, Limit = 2, NextToken = token });
            foreach (var item in page.Items)
            {
                seen.Add(item["sk"].N);

                // Drain as we go: the token's item is gone by the time the next page is asked for.
                await client.DeleteItemAsync("tbl", Item("A", int.Parse(item["sk"].N, CultureInfo.InvariantCulture)));
            }

            token = page.NextToken;
        }
        while (token is not null);

        seen.ShouldBe(["5", "4", "3", "2", "1"]);
    }

    [Test]
    public async Task ExecuteStatement_IndexPagesResumeInIndexOrderAfterTheLastItemIsRekeyed()
    {
        var bus = new InMemoryAwsBus();
        using var client = await CreateIndexedTableAsync(bus);

        // Index order (rank) is the reverse of table order (sk).
        for (var sk = 1; sk <= 5; sk++)
        {
            await client.PutItemAsync("tbl", IndexedItem(sk, rank: 10 - sk));
        }

        const string Statement = "SELECT * FROM \"tbl\".\"byStatus\" WHERE status = 'PENDING'";
        var seen = new List<string>();
        string? token = null;
        do
        {
            var page = await client.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = Statement, Limit = 2, NextToken = token });
            foreach (var item in page.Items)
            {
                seen.Add(item["sk"].N);

                // Processing an item takes it out of the index's partition.
                await client.UpdateItemAsync(new UpdateItemRequest
                {
                    TableName = "tbl",
                    Key = Item("A", int.Parse(item["sk"].N, CultureInfo.InvariantCulture)),
                    UpdateExpression = "SET #s = :done",
                    ExpressionAttributeNames = new Dictionary<string, string> { ["#s"] = "status" },
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":done"] = new("DONE") },
                });
            }

            token = page.NextToken;
        }
        while (token is not null);

        seen.ShouldBe(["5", "4", "3", "2", "1"]);
    }

    [Test]
    public async Task ExecuteStatement_OrderByWithThePartitionKeyMatchedByIn()
    {
        var bus = new InMemoryAwsBus();
        using var client = await CreateTableAsync(bus);
        await client.PutItemAsync("tbl", Item("A", 1));
        await client.PutItemAsync("tbl", Item("B", 1));
        await client.PutItemAsync("tbl", Item("C", 1));

        var result = await client.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = "SELECT pk FROM \"tbl\" WHERE pk IN ['A', 'C'] ORDER BY pk DESC",
        });

        result.Items.Select(i => i["pk"].S).ShouldBe(["C", "A"]);
    }

    [Test]
    public async Task ExecuteStatement_OrderByKeepsTiesInIndexOrder()
    {
        var bus = new InMemoryAwsBus();
        using var client = await CreateIndexedTableAsync(bus);
        for (var sk = 1; sk <= 40; sk++)
        {
            await client.PutItemAsync("tbl", IndexedItem(sk, rank: 7));
        }

        var result = await client.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = "SELECT sk FROM \"tbl\".\"byStatus\" WHERE status = 'PENDING' ORDER BY rank DESC",
        });

        result.Items.Select(i => int.Parse(i["sk"].N, CultureInfo.InvariantCulture)).ShouldBe(Enumerable.Range(1, 40));
    }

    [Test]
    public async Task TransactUpdate_ThatOnlyRemovesCreatesNoItem()
    {
        var bus = new InMemoryAwsBus();
        using var client = await CreateTableAsync(bus);

        await client.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems = [new TransactWriteItem { Update = new Update { TableName = "tbl", Key = Item("missing", 1), UpdateExpression = "REMOVE a" } }],
        });

        (await client.GetItemAsync("tbl", Item("missing", 1))).IsItemSet.ShouldBeFalse();
    }

    [Test]
    public async Task UpdateTable_RefusedLeavesTheTableUnchanged()
    {
        var bus = new InMemoryAwsBus();
        using var client = await CreateTableAsync(bus, viewType: StreamViewType.NEW_IMAGE, billingMode: BillingMode.PROVISIONED);

        await Should.ThrowAsync<AmazonDynamoDBException>(() => client.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = "tbl",
            BillingMode = BillingMode.PAY_PER_REQUEST,
            StreamSpecification = new StreamSpecification { StreamEnabled = true, StreamViewType = StreamViewType.NEW_IMAGE },
        }));

        var table = (await client.DescribeTableAsync("tbl")).Table;
        (table.BillingModeSummary?.BillingMode ?? BillingMode.PROVISIONED).ShouldBe(BillingMode.PROVISIONED);
        table.ProvisionedThroughput.ReadCapacityUnits.ShouldBe(5);
    }

    [Test]
    public async Task UpdateTable_FailedIndexCreateDeclaresNoAttribute()
    {
        var bus = new InMemoryAwsBus();
        using var client = await CreateTableAsync(bus);
        UpdateTableRequest CreateIndex(ScalarAttributeType type, Projection projection) => new()
        {
            TableName = "tbl",
            AttributeDefinitions = [new AttributeDefinition("gsiKey", type)],
            GlobalSecondaryIndexUpdates =
            [
                new GlobalSecondaryIndexUpdate
                {
                    Create = new CreateGlobalSecondaryIndexAction
                    {
                        IndexName = "gsi",
                        KeySchema = [new KeySchemaElement("gsiKey", KeyType.HASH)],
                        Projection = projection,
                    },
                },
            ],
        };

        await Should.ThrowAsync<AmazonDynamoDBException>(() =>
            client.UpdateTableAsync(CreateIndex(ScalarAttributeType.S, new Projection { ProjectionType = ProjectionType.INCLUDE })));
        (await client.DescribeTableAsync("tbl")).Table.AttributeDefinitions.Select(a => a.AttributeName).ShouldBe(["pk", "sk"], ignoreOrder: true);

        await client.UpdateTableAsync(CreateIndex(ScalarAttributeType.N, new Projection { ProjectionType = ProjectionType.ALL }));
        (await client.DescribeTableAsync("tbl")).Table.GlobalSecondaryIndexes.Single().IndexName.ShouldBe("gsi");
    }

    [Test]
    public async Task UpdateItem_AttributeNamedWithADotDoesNotOverlapTheNestedPath()
    {
        var bus = new InMemoryAwsBus();
        using var client = await CreateTableAsync(bus);
        var item = Item("A", 1);
        item["a"] = new AttributeValue { M = new Dictionary<string, AttributeValue> { ["b"] = new("old") } };
        await client.PutItemAsync("tbl", item);

        await client.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = "tbl",
            Key = Item("A", 1),
            UpdateExpression = "SET #ab = :x, a.b = :y",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#ab"] = "a.b" },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":x"] = new("top"), [":y"] = new("nested") },
        });

        var stored = (await client.GetItemAsync("tbl", Item("A", 1))).Item;
        stored["a.b"].S.ShouldBe("top");
        stored["a"].M["b"].S.ShouldBe("nested");
    }

    [Test]
    public async Task CreateTable_IndexNameEndingInANewlineIsRefused()
    {
        var bus = new InMemoryAwsBus();
        using var client = bus.CreateDynamoDbClient();

        var refusal = await Should.ThrowAsync<AmazonDynamoDBException>(() => client.CreateTableAsync(new CreateTableRequest
        {
            TableName = "tbl",
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("g", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
#pragma warning disable DynamoDB1002 // The point: a name the model's pattern refuses.
                    IndexName = "gsi\n",
#pragma warning restore DynamoDB1002
                    KeySchema = [new KeySchemaElement("g", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
        }));

        refusal.ErrorCode.ShouldBe("ValidationException");
        refusal.Message.ShouldContain("regular expression pattern");
    }

    private static async Task<AmazonDynamoDBClient> CreateIndexedTableAsync(InMemoryAwsBus bus)
    {
        var client = bus.CreateDynamoDbClient();
        await client.CreateTableAsync(new CreateTableRequest
        {
            TableName = "tbl",
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.N),
                new AttributeDefinition("status", ScalarAttributeType.S),
                new AttributeDefinition("rank", ScalarAttributeType.N),
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = "byStatus",
                    KeySchema = [new KeySchemaElement("status", KeyType.HASH), new KeySchemaElement("rank", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
        });
        return client;
    }

    private static Dictionary<string, AttributeValue> IndexedItem(int sk, int rank)
    {
        var item = Item("A", sk, ("status", "PENDING"));
        item["rank"] = new AttributeValue { N = rank.ToString(CultureInfo.InvariantCulture) };
        return item;
    }

    /// <summary>A <see cref="TimeProvider"/> that counts the timers made through it that are not yet disposed.</summary>
    private sealed class TimerCountingTimeProvider(TimeProvider inner) : TimeProvider
    {
        private int _liveTimers;

        public int LiveTimers => Volatile.Read(ref _liveTimers);

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override long GetTimestamp() => inner.GetTimestamp();

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Increment(ref _liveTimers);
            return new CountedTimer(inner.CreateTimer(callback, state, dueTime, period), () => Interlocked.Decrement(ref _liveTimers));
        }

        private sealed class CountedTimer(ITimer timer, Action disposed) : ITimer
        {
            private int _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period) => timer.Change(dueTime, period);

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    disposed();
                }

                timer.Dispose();
            }

            public async ValueTask DisposeAsync()
            {
                Dispose();
                await Task.CompletedTask.ConfigureAwait(false);
            }
        }
    }
}

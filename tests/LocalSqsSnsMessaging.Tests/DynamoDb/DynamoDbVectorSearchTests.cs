using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DataModel;
using Amazon.DynamoDBv2.DocumentModel;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace LocalSqsSnsMessaging.Tests.DynamoDb;

/// <summary>
/// Vector indexes and <c>SearchVectors</c> through the SDK's object persistence model
/// (<c>DynamoDBContext</c>), its Document Model and the low-level client, and the life of a vector
/// index that UpdateTable adds on the bus's clock.
/// </summary>
public sealed class DynamoDbVectorSearchTests
{
    private const string IndexName = "by-embedding";

    [DynamoDBTable("Articles")]
    public sealed class Article
    {
        [DynamoDBHashKey]
        public string Id { get; set; } = string.Empty;

        public string Category { get; set; } = string.Empty;

#pragma warning disable CA1002, CA2227 // DynamoDBContext maps a settable list property
        [DynamoDBSearchVector(IndexName)]
        public List<float> Embedding { get; set; } = [];
#pragma warning restore CA1002, CA2227
    }

    private static async Task<AmazonDynamoDBClient> CreateArticlesTableAsync(InMemoryAwsBus bus)
    {
        var client = bus.CreateDynamoDbClient();
        await client.CreateTableAsync(new CreateTableRequest
        {
            TableName = "Articles",
            KeySchema = [new KeySchemaElement("Id", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("Id", ScalarAttributeType.S), new AttributeDefinition("Category", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            VectorIndexes =
            [
                new VectorIndex
                {
                    IndexName = IndexName,
                    VectorAttribute = new VectorAttributeDefinition { AttributeName = "Embedding" },
                    Dimensions = 3,
                    DistanceFunction = VectorDistanceFunction.COSINE,
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                    SearchSchema = [new SearchSchemaElement { AttributeName = "Category", SearchSchemaElementType = SearchSchemaElementType.INLINE_FILTER }],
                },
            ],
        });
        return client;
    }

    private static Dictionary<string, AttributeValue> Item(string id, string category, params float[] embedding) => new()
    {
        ["Id"] = new AttributeValue(id),
        ["Category"] = new AttributeValue(category),
        ["Embedding"] = new AttributeValue { L = [.. embedding.Select(v => new AttributeValue { N = v.ToString("R", CultureInfo.InvariantCulture) })] },
    };

    [Test]
    public async Task Context_SavesVectorsAndFindsTheNearest()
    {
        var bus = new InMemoryAwsBus();
        using var client = await CreateArticlesTableAsync(bus);
        // The SDK maps a search vector only with the V2 conversion (a list of numbers, not a number set).
        using var context = new DynamoDBContextBuilder()
            .WithDynamoDBClient(() => client)
            .ConfigureContext(c => c.Conversion = DynamoDBEntryConversion.V2)
            .Build();
        await context.SaveAsync(new Article { Id = "east", Category = "news", Embedding = [1, 0, 0] });
        await context.SaveAsync(new Article { Id = "north", Category = "news", Embedding = [0, 1, 0] });
        await context.SaveAsync(new Article { Id = "west", Category = "sport", Embedding = [-1, 0, 0] });

        var nearest = await context.SearchVectorsAsync<Article>([0.9f, 0.1f, 0], 2, new SearchVectorsConfig { IndexName = IndexName }).GetAsync();

        nearest.Select(r => r.Item.Id).ShouldBe(["east", "north"]);
        nearest[0].Score!.Value.ShouldBeLessThan(nearest[1].Score!.Value);

        var sport = new ContextExpression();
        sport.SetFilter<Article>(a => a.Category == "sport");
        var filtered = await context.SearchVectorsAsync<Article>([0.9f, 0.1f, 0], 3, new SearchVectorsConfig
        {
            IndexName = IndexName,
            SearchConditionExpression = sport,
        }).GetAsync();

        filtered.Single().Item.Id.ShouldBe("west");
    }

    [Test]
    public async Task DocumentModel_SearchesWithAFilterAndProjection()
    {
        var bus = new InMemoryAwsBus();
        using var client = await CreateArticlesTableAsync(bus);
        await client.PutItemAsync("Articles", Item("a", "news", 1, 0, 0));
        await client.PutItemAsync("Articles", Item("b", "news", 0.5f, 0.5f, 0));
        await client.PutItemAsync("Articles", Item("c", "sport", 1, 0.1f, 0));
        var table = new TableBuilder(client, "Articles").AddHashKey("Id", DynamoDBEntryType.String).Build();

        var search = table.SearchVectors(new SearchVectorsOperationRequest
        {
            IndexName = IndexName,
            SearchVector = [1, 0, 0],
            TopK = 10,
            SearchConditionExpression = new Expression
            {
                ExpressionStatement = "Category = :category",
                ExpressionAttributeValues = { [":category"] = "news" },
            },
            ProjectionExpression = new Expression { ExpressionStatement = "Id" },
        });
        var matches = await search.GetAsync();

        matches.Select(m => m.Document["Id"].AsString()).ShouldBe(["a", "b"]);
        matches.ShouldAllBe(m => m.Document.Keys.Count == 1);
        matches[0].Score.ShouldBe(0);
    }

    [Test]
    public async Task LowLevel_ScoresEachDistanceFunctionAndMetersCapacity()
    {
        var bus = new InMemoryAwsBus();
        using var client = await CreateArticlesTableAsync(bus);
        var put = await client.PutItemAsync(new PutItemRequest
        {
            TableName = "Articles",
            Item = Item("a", "news", 1, 0, 0),
            ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES,
        });
        put.ConsumedCapacity.VectorIndexes[IndexName].VectorWriteRequestBytes.ShouldBe(1024);

        var response = await client.SearchVectorsAsync(new SearchVectorsRequest
        {
            TableName = "Articles",
            IndexName = IndexName,
            SearchVector = [new AttributeValue { N = "0" }, new AttributeValue { N = "1" }, new AttributeValue { N = "0" }],
            TopK = 1,
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        });

        response.SearchResults.Single().Score.ShouldBe(1.0);
        response.ConsumedCapacity.VectorSearchRequestBytes.ShouldBe(1024);

        // A vector index is read only by SearchVectors.
        var query = await Should.ThrowAsync<AmazonDynamoDBException>(() => client.ScanAsync(new ScanRequest { TableName = "Articles", IndexName = IndexName }));
        query.Message.ShouldBe("Scan operation not supported on this index type.");
    }

    [Test]
    public async Task WriteOfTheWrongDimensionIsRefused()
    {
        var bus = new InMemoryAwsBus();
        using var client = await CreateArticlesTableAsync(bus);

        var error = await Should.ThrowAsync<AmazonDynamoDBException>(() => client.PutItemAsync("Articles", Item("a", "news", 1, 0)));

        error.ErrorCode.ShouldBe("ValidationException");
        error.Message.ShouldBe("One or more parameter values were invalid. Invalid size for parameter Embedding, Expected: 3, Actual: 2 IndexName: by-embedding");
    }

    [Test]
    public async Task UpdateTable_IndexIsCreatingThenBackfillingThenActiveOnTheBusClock()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));
        var bus = new InMemoryAwsBus { TimeProvider = time, DynamoDbVectorIndexCreationDelay = TimeSpan.FromMinutes(10) };
        using var client = bus.CreateDynamoDbClient();
        await client.CreateTableAsync(new CreateTableRequest
        {
            TableName = "Late",
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        await client.PutItemAsync("Late", new Dictionary<string, AttributeValue> { ["pk"] = new("before"), ["v"] = new() { L = [new() { N = "1" }, new() { N = "0" }] } });

        var created = await client.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = "Late",
            VectorIndexUpdates =
            [
                new VectorIndexUpdate
                {
                    Create = new CreateVectorIndexAction
                    {
                        IndexName = "late",
                        VectorAttribute = new VectorAttributeDefinition { AttributeName = "v" },
                        Dimensions = 2,
                        DistanceFunction = VectorDistanceFunction.EUCLIDEAN,
                        Projection = new Projection { ProjectionType = ProjectionType.KEYS_ONLY },
                    },
                },
            ],
        });
        created.TableDescription.VectorIndexes.Single().IndexStatus.ShouldBe(IndexStatus.CREATING);

        Task<SearchVectorsResponse> Search() => client.SearchVectorsAsync(new SearchVectorsRequest
        {
            TableName = "Late",
            IndexName = "late",
            SearchVector = [new() { N = "0" }, new() { N = "0" }],
            TopK = 10,
        });

        (await Should.ThrowAsync<AmazonDynamoDBException>(Search)).Message.ShouldBe("The table does not have the specified index: late");
        (await Should.ThrowAsync<ResourceInUseException>(() => client.DeleteTableAsync("Late"))).Message
            .ShouldBe("Attempt to change a resource which is still in use: Cannot delete table while indexes are being created, updated, or deleted.");

        time.Advance(TimeSpan.FromMinutes(5));
        var backfilling = (await client.DescribeTableAsync("Late")).Table.VectorIndexes.Single();
        backfilling.IndexStatus.ShouldBe(IndexStatus.CREATING);
        backfilling.Backfilling.ShouldBe(true);
        await client.PutItemAsync("Late", new Dictionary<string, AttributeValue> { ["pk"] = new("during"), ["v"] = new() { L = [new() { N = "0" }, new() { N = "2" }] } });

        time.Advance(TimeSpan.FromMinutes(5));
        var active = (await client.DescribeTableAsync("Late")).Table.VectorIndexes.Single();
        active.IndexStatus.ShouldBe(IndexStatus.ACTIVE);
        active.Backfilling.ShouldBeNull();
        active.ItemCount.ShouldBe(2);

        var results = (await Search()).SearchResults;
        results.Select(r => r.Item["pk"].S).ShouldBe(["before", "during"]);
        results.Select(r => r.Score).ShouldBe([1.0, 2.0]);

        var deleted = await client.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = "Late",
            VectorIndexUpdates = [new VectorIndexUpdate { Delete = new DeleteVectorIndexAction { IndexName = "late" } }],
        });
        deleted.TableDescription.VectorIndexes.Single().IndexStatus.ShouldBe(IndexStatus.DELETING);
        (await client.DescribeTableAsync("Late")).Table.VectorIndexes.ShouldBeNull();
    }

    [Test]
    public async Task UpdateTable_GlobalIndexChangeWaitsForACreatingVectorIndex()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));
        var bus = new InMemoryAwsBus { TimeProvider = time, DynamoDbVectorIndexCreationDelay = TimeSpan.FromMinutes(10) };
        using var client = bus.CreateDynamoDbClient();
        await client.CreateTableAsync(new CreateTableRequest
        {
            TableName = "Busy",
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("g", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = "by-g",
                    KeySchema = [new KeySchemaElement("g", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
        });
        await client.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = "Busy",
            VectorIndexUpdates =
            [
                new VectorIndexUpdate
                {
                    Create = new CreateVectorIndexAction
                    {
                        IndexName = "vectors",
                        VectorAttribute = new VectorAttributeDefinition { AttributeName = "v" },
                        Dimensions = 2,
                        DistanceFunction = VectorDistanceFunction.EUCLIDEAN,
                        Projection = new Projection { ProjectionType = ProjectionType.KEYS_ONLY },
                    },
                },
            ],
        });

        Task<UpdateTableResponse> CreateGlobalIndex() => client.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = "Busy",
            AttributeDefinitions = [new AttributeDefinition("h", ScalarAttributeType.S)],
            GlobalSecondaryIndexUpdates =
            [
                new GlobalSecondaryIndexUpdate
                {
                    Create = new CreateGlobalSecondaryIndexAction
                    {
                        IndexName = "by-h",
                        KeySchema = [new KeySchemaElement("h", KeyType.HASH)],
                        Projection = new Projection { ProjectionType = ProjectionType.ALL },
                    },
                },
            ],
        });

        const string OneAtATime = "Subscriber limit exceeded: Only 1 online index can be created or deleted simultaneously per table";
        (await Should.ThrowAsync<LimitExceededException>(CreateGlobalIndex)).Message.ShouldBe(OneAtATime);
        (await Should.ThrowAsync<LimitExceededException>(() => client.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = "Busy",
            GlobalSecondaryIndexUpdates = [new GlobalSecondaryIndexUpdate { Delete = new DeleteGlobalSecondaryIndexAction { IndexName = "by-g" } }],
        }))).Message.ShouldBe(OneAtATime);

        time.Advance(TimeSpan.FromMinutes(10));
        var created = await CreateGlobalIndex();
        created.TableDescription.GlobalSecondaryIndexes.Select(i => i.IndexName).ShouldContain("by-h");
    }

    [Test]
    public async Task RestoreTableFromBackup_ProvisionedOverrideKeepingAVectorIndexIsRefused()
    {
        var bus = new InMemoryAwsBus();
        using var client = await CreateArticlesTableAsync(bus);
        var backupArn = (await client.CreateBackupAsync(new CreateBackupRequest { TableName = "Articles", BackupName = "articles" })).BackupDetails.BackupArn;

        RestoreTableFromBackupRequest Restore(string target, List<VectorIndex>? vectorIndexes) => new()
        {
            BackupArn = backupArn,
            TargetTableName = target,
            BillingModeOverride = BillingMode.PROVISIONED,
            ProvisionedThroughputOverride = new ProvisionedThroughput(5, 5),
            VectorIndexOverride = vectorIndexes,
        };

        var error = await Should.ThrowAsync<AmazonDynamoDBException>(() => client.RestoreTableFromBackupAsync(Restore("Provisioned", null)));
        error.ErrorCode.ShouldBe("ValidationException");
        error.Message.ShouldBe("One or more parameter values were invalid: Vector indexes are only supported for PAY_PER_REQUEST tables");
        (await client.ListTablesAsync()).TableNames.ShouldNotContain("Provisioned");

        // Leaving every vector index out lets the restored table be provisioned.
        var restored = (await client.RestoreTableFromBackupAsync(Restore("ProvisionedWithoutVectors", []))).TableDescription;
        restored.BillingModeSummary?.BillingMode.ShouldNotBe(BillingMode.PAY_PER_REQUEST);
        restored.VectorIndexes.ShouldBeNull();
    }
}

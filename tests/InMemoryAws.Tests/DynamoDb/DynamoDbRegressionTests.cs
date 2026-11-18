using AmazonDynamoDBStreamsClient = Amazon.DynamoDBStreams.AmazonDynamoDBStreamsClient;
using AmazonDynamoDBStreamsConfig = Amazon.DynamoDBStreams.AmazonDynamoDBStreamsConfig;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Streams = Amazon.DynamoDBStreams.Model;

namespace InMemoryAws.Tests.DynamoDb;

/// <summary>Regression tests for DynamoDB behaviour the evaluation suite doesn't cover.</summary>
public sealed class DynamoDbRegressionTests
{
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private static async Task<AmazonDynamoDBClient> CreateTableAsync(
        InMemoryAwsCloud cloud,
        string name = "tbl",
        StreamViewType? viewType = null,
        BillingMode? billingMode = null)
    {
        var client = cloud.CreateDynamoDbClient();
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

    private static AmazonDynamoDBStreamsClient CreateStreamsClient(InMemoryAwsCloud cloud) =>
        new(new AnonymousAWSCredentials(), new AmazonDynamoDBStreamsConfig
        {
            ServiceURL = $"https://streams.dynamodb.{cloud.CurrentRegion}.amazonaws.com",
            AuthenticationRegion = cloud.CurrentRegion,
            MaxErrorRetry = 0,
            HttpClientFactory = cloud.CreateAwsHttpClientFactory(),
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
        var cloud = new InMemoryAwsCloud { TimeProvider = time };
        using var client = await CreateTableAsync(cloud);
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
        var cloud = new InMemoryAwsCloud();
        using var client = await CreateTableAsync(cloud, viewType: StreamViewType.NEW_AND_OLD_IMAGES);
        using var streams = CreateStreamsClient(cloud);

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
        var cloud = new InMemoryAwsCloud { TimeProvider = new FakeTimeProvider(Start) };
        using var client = await CreateTableAsync(cloud, viewType: StreamViewType.NEW_IMAGE);
        using var streams = CreateStreamsClient(cloud);
        await client.PutItemAsync("tbl", Item("old", 1));
        var oldStreamArn = (await client.DescribeTableAsync("tbl")).Table.LatestStreamArn;

        await client.DeleteTableAsync("tbl");
        using var recreated = await CreateTableAsync(cloud, viewType: StreamViewType.NEW_IMAGE);
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
        var cloud = new InMemoryAwsCloud();
        using var client = await CreateTableAsync(cloud);
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
        var cloud = new InMemoryAwsCloud();
        using var client = await CreateIndexedTableAsync(cloud);

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
        var cloud = new InMemoryAwsCloud();
        using var client = await CreateTableAsync(cloud);
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
        var cloud = new InMemoryAwsCloud();
        using var client = await CreateIndexedTableAsync(cloud);
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
    public async Task ExecuteTransaction_ExistsWithAFunctionInItsSelectListIsRefused()
    {
        var cloud = new InMemoryAwsCloud();
        using var client = await CreateTableAsync(cloud);
        await client.PutItemAsync("tbl", Item("A", 1));
        await client.PutItemAsync("tbl", Item("B", 1));

        var error = await Should.ThrowAsync<AmazonDynamoDBException>(() => client.ExecuteTransactionAsync(new ExecuteTransactionRequest
        {
            TransactStatements =
            [
                new ParameterizedStatement { Statement = "EXISTS(SELECT COUNT(*) FROM \"tbl\" WHERE pk = 'A' AND sk = 1)" },
                new ParameterizedStatement { Statement = "UPDATE \"tbl\" SET note = 'changed' WHERE pk = 'B' AND sk = 1" },
            ],
        }));

        error.ErrorCode.ShouldBe("ValidationException");
        error.Message.ShouldContain("Unexpected path component");
        (await client.GetItemAsync("tbl", Item("B", 1))).Item.ShouldNotContainKey("note");
    }

    [Test]
    public async Task TransactUpdate_ThatOnlyRemovesCreatesNoItem()
    {
        var cloud = new InMemoryAwsCloud();
        using var client = await CreateTableAsync(cloud);

        await client.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems = [new TransactWriteItem { Update = new Update { TableName = "tbl", Key = Item("missing", 1), UpdateExpression = "REMOVE a" } }],
        });

        (await client.GetItemAsync("tbl", Item("missing", 1))).IsItemSet.ShouldBeFalse();
    }

    [Test]
    public async Task UpdateTable_RefusedLeavesTheTableUnchanged()
    {
        var cloud = new InMemoryAwsCloud();
        using var client = await CreateTableAsync(cloud, viewType: StreamViewType.NEW_IMAGE, billingMode: BillingMode.PROVISIONED);

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
        var cloud = new InMemoryAwsCloud();
        using var client = await CreateTableAsync(cloud);
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
        var cloud = new InMemoryAwsCloud();
        using var client = await CreateTableAsync(cloud);
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
        var cloud = new InMemoryAwsCloud();
        using var client = cloud.CreateDynamoDbClient();

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

    [Test]
    public async Task BatchWriteItem_WriteRequestWithoutItsKeyOrItemIsRefused()
    {
        var cloud = new InMemoryAwsCloud();
        using var client = await CreateTableAsync(cloud);

        foreach (var write in new[] { new WriteRequest { DeleteRequest = new DeleteRequest() }, new WriteRequest { PutRequest = new PutRequest() } })
        {
            var refusal = await Should.ThrowAsync<AmazonDynamoDBException>(() => client.BatchWriteItemAsync(new BatchWriteItemRequest
            {
                RequestItems = new Dictionary<string, List<WriteRequest>> { ["tbl"] = [write] },
            }));

            refusal.ErrorCode.ShouldBe("ValidationException");
            refusal.Message.ShouldContain("Member must not be null");
        }
    }

    [Test]
    public async Task RestoreTableToPointInTime_RefusesATimeBeforeTheRecoveryPeriod()
    {
        var time = new FakeTimeProvider(Start);
        var cloud = new InMemoryAwsCloud { TimeProvider = time };
        using var client = await CreateTableAsync(cloud);
        await client.UpdateContinuousBackupsAsync(new UpdateContinuousBackupsRequest
        {
            TableName = "tbl",
            PointInTimeRecoverySpecification = new PointInTimeRecoverySpecification { PointInTimeRecoveryEnabled = true, RecoveryPeriodInDays = 1 },
        });
        time.Advance(TimeSpan.FromSeconds(1));
        await client.PutItemAsync("tbl", Item("A", 1, ("v", "1")));
        time.Advance(TimeSpan.FromDays(2));
        await client.PutItemAsync("tbl", Item("A", 1, ("v", "2")));

        var description = (await client.DescribeContinuousBackupsAsync(new DescribeContinuousBackupsRequest { TableName = "tbl" }))
            .ContinuousBackupsDescription.PointInTimeRecoveryDescription;
        description.EarliestRestorableDateTime.ShouldNotBeNull().ShouldBeGreaterThanOrEqualTo(time.GetUtcNow().UtcDateTime.AddDays(-1));

        var refusal = await Should.ThrowAsync<AmazonDynamoDBException>(() => client.RestoreTableToPointInTimeAsync(new RestoreTableToPointInTimeRequest
        {
            SourceTableName = "tbl",
            TargetTableName = "restored",
            RestoreDateTime = Start.UtcDateTime,
        }));
        refusal.ErrorCode.ShouldBe("InvalidRestoreTimeException");

        // The earliest time it reports is one it can restore to, with every later change undone.
        await client.RestoreTableToPointInTimeAsync(new RestoreTableToPointInTimeRequest
        {
            SourceTableName = "tbl",
            TargetTableName = "restored",
            RestoreDateTime = description.EarliestRestorableDateTime,
        });
        (await client.GetItemAsync("restored", Item("A", 1))).Item["v"].S.ShouldBe("1");
    }

    [Test]
    public async Task ResourcePolicy_OfAStreamIsKeptApartFromItsTables()
    {
        var cloud = new InMemoryAwsCloud();
        using var client = await CreateTableAsync(cloud, viewType: StreamViewType.NEW_IMAGE);
        var table = (await client.DescribeTableAsync("tbl")).Table;
        const string TablePolicy = """{"Version":"2012-10-17","Statement":[{"Effect":"Allow","Principal":{"AWS":"arn:aws:iam::000000000000:root"},"Action":"dynamodb:GetItem","Resource":"*"}]}""";
        const string StreamPolicy = """{"Version":"2012-10-17","Statement":[{"Effect":"Allow","Principal":{"AWS":"arn:aws:iam::000000000000:root"},"Action":"dynamodb:GetRecords","Resource":"*"}]}""";

        await client.PutResourcePolicyAsync(new PutResourcePolicyRequest { ResourceArn = table.TableArn, Policy = TablePolicy });
        await client.PutResourcePolicyAsync(new PutResourcePolicyRequest { ResourceArn = table.LatestStreamArn, Policy = StreamPolicy });

        (await client.GetResourcePolicyAsync(new GetResourcePolicyRequest { ResourceArn = table.TableArn })).Policy.ShouldBe(TablePolicy);
        (await client.GetResourcePolicyAsync(new GetResourcePolicyRequest { ResourceArn = table.LatestStreamArn })).Policy.ShouldBe(StreamPolicy);

        await client.DeleteResourcePolicyAsync(new DeleteResourcePolicyRequest { ResourceArn = table.LatestStreamArn });
        (await client.GetResourcePolicyAsync(new GetResourcePolicyRequest { ResourceArn = table.TableArn })).Policy.ShouldBe(TablePolicy);
    }

    [Test]
    public async Task PutResourcePolicy_NoPolicyRevisionAttachesOnlyWhereThereIsNone()
    {
        var cloud = new InMemoryAwsCloud();
        using var client = await CreateTableAsync(cloud);
        var arn = (await client.DescribeTableAsync("tbl")).Table.TableArn;
        const string Policy = """{"Version":"2012-10-17","Statement":[{"Effect":"Allow","Principal":{"AWS":"arn:aws:iam::000000000000:root"},"Action":"dynamodb:GetItem","Resource":"*"}]}""";

        var attached = await client.PutResourcePolicyAsync(new PutResourcePolicyRequest { ResourceArn = arn, Policy = Policy, ExpectedRevisionId = "NO_POLICY" });
        attached.RevisionId.ShouldNotBeNullOrEmpty();

        var refusal = await Should.ThrowAsync<AmazonDynamoDBException>(() => client.PutResourcePolicyAsync(new PutResourcePolicyRequest
        {
            ResourceArn = arn,
            Policy = Policy,
            ExpectedRevisionId = "NO_POLICY",
        }));
        refusal.ErrorCode.ShouldBe("PolicyNotFoundException");
    }

    [Test]
    public async Task CreateTable_ResourcePolicyHasARevision()
    {
        var cloud = new InMemoryAwsCloud();
        using var client = cloud.CreateDynamoDbClient();
        await client.CreateTableAsync(new CreateTableRequest
        {
            TableName = "tbl",
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            ResourcePolicy = """{"Version":"2012-10-17","Statement":[{"Effect":"Allow","Principal":{"AWS":"arn:aws:iam::000000000000:root"},"Action":"dynamodb:GetItem","Resource":"*"}]}""",
        });
        var arn = (await client.DescribeTableAsync("tbl")).Table.TableArn;

        var revision = (await client.GetResourcePolicyAsync(new GetResourcePolicyRequest { ResourceArn = arn })).RevisionId;

        revision.ShouldNotBeNullOrEmpty();
        await client.DeleteResourcePolicyAsync(new DeleteResourcePolicyRequest { ResourceArn = arn, ExpectedRevisionId = revision });
    }

    [Test]
    public async Task RestoreTableFromBackup_IndexOverrideLeavesIndexesOut()
    {
        var cloud = new InMemoryAwsCloud();
        using var client = await CreateIndexedTableAsync(cloud);
        await client.PutItemAsync("tbl", IndexedItem(1, rank: 7));
        var backupArn = (await client.CreateBackupAsync(new CreateBackupRequest { TableName = "tbl", BackupName = "backup" })).BackupDetails.BackupArn;

        await client.RestoreTableFromBackupAsync(new RestoreTableFromBackupRequest
        {
            BackupArn = backupArn,
            TargetTableName = "restored",
            GlobalSecondaryIndexOverride = [],
        });

        var restored = (await client.DescribeTableAsync("restored")).Table;
        (restored.GlobalSecondaryIndexes ?? []).ShouldBeEmpty();
        restored.AttributeDefinitions.Select(a => a.AttributeName).ShouldBe(["pk", "sk"], ignoreOrder: true);
        (await client.GetItemAsync("restored", IndexedItem(1, rank: 7).Where(a => a.Key is "pk" or "sk").ToDictionary())).IsItemSet.ShouldBeTrue();
    }

    [Test]
    public async Task RestoreTableFromBackup_UnderTheDeletedTablesNameIsCreatedAnew()
    {
        var time = new FakeTimeProvider(Start);
        var cloud = new InMemoryAwsCloud { TimeProvider = time };
        using var client = await CreateTableAsync(cloud);
        var backupArn = (await client.CreateBackupAsync(new CreateBackupRequest { TableName = "tbl", BackupName = "backup" })).BackupDetails.BackupArn;
        await client.DeleteTableAsync("tbl");
        time.Advance(TimeSpan.FromHours(1));

        var restored = (await client.RestoreTableFromBackupAsync(new RestoreTableFromBackupRequest { BackupArn = backupArn, TargetTableName = "tbl" })).TableDescription;

        restored.CreationDateTime.ShouldBe(time.GetUtcNow().UtcDateTime);
    }

    [Test]
    public async Task TransactWriteItems_ChargesTheIndexesItWrites()
    {
        var cloud = new InMemoryAwsCloud();
        using var client = await CreateIndexedTableAsync(cloud);

        var result = await client.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems = [new TransactWriteItem { Put = new Put { TableName = "tbl", Item = IndexedItem(1, rank: 7) } }],
            ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES,
        });

        var consumed = result.ConsumedCapacity.Single();
        consumed.GlobalSecondaryIndexes["byStatus"].CapacityUnits.ShouldBe(1);
        consumed.CapacityUnits.ShouldBe(consumed.Table.CapacityUnits + 1);
    }

    [Test]
    public async Task ExecuteStatement_InsertChargesTheIndexesItWrites()
    {
        var cloud = new InMemoryAwsCloud();
        using var client = await CreateIndexedTableAsync(cloud);

        var result = await client.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = "INSERT INTO \"tbl\" VALUE {'pk': 'A', 'sk': 1, 'status': 'PENDING', 'rank': 7}",
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        });

        result.ConsumedCapacity.CapacityUnits.ShouldBe(2);
    }

    [Test]
    public async Task ExecuteStatement_InOnThePartitionKeyReadsOnlyThosePartitions()
    {
        var cloud = new InMemoryAwsCloud();
        using var client = await CreateTableAsync(cloud);
        foreach (var pk in new[] { "A", "B", "C", "D", "E" })
        {
            await client.PutItemAsync("tbl", Item(pk, 1));
        }

        var result = await client.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = "SELECT pk FROM \"tbl\" WHERE pk IN ['E', 'C']",
            Limit = 1,
        });

        result.Items.Select(i => i["pk"].S).ShouldBe(["C"]);
        var next = await client.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = "SELECT pk FROM \"tbl\" WHERE pk IN ['E', 'C']",
            NextToken = result.NextToken,
        });
        next.Items.Select(i => i["pk"].S).ShouldBe(["E"]);
    }

    [Test]
    public async Task UpdateItem_RefusesAnItemTooLargeWithItsLocalIndexEntry()
    {
        var cloud = new InMemoryAwsCloud();
        using var client = cloud.CreateDynamoDbClient();
        await client.CreateTableAsync(new CreateTableRequest
        {
            TableName = "tbl",
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.N),
                new AttributeDefinition("lsk", ScalarAttributeType.S),
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            LocalSecondaryIndexes =
            [
                new LocalSecondaryIndex
                {
                    IndexName = "byLsk",
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("lsk", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
        });
        await client.PutItemAsync("tbl", Item("A", 1, ("lsk", "x")));

        // About 300 KB: under the limit alone, over it with the index entry that copies it.
        var refusal = await Should.ThrowAsync<AmazonDynamoDBException>(() => client.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = "tbl",
            Key = Item("A", 1),
            UpdateExpression = "SET big = :big",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":big"] = new(new string('x', 300 * 1024)) },
        }));

        refusal.ErrorCode.ShouldBe("ValidationException");
        refusal.Message.ShouldContain("Item size to update has exceeded the maximum allowed size");
    }

    [Test]
    public async Task UpdateTable_LeavingOnDemandNeedsThroughputForEachGlobalIndex()
    {
        var cloud = new InMemoryAwsCloud();
        using var client = await CreateIndexedTableAsync(cloud);

        var refusal = await Should.ThrowAsync<AmazonDynamoDBException>(() => client.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = "tbl",
            BillingMode = BillingMode.PROVISIONED,
            ProvisionedThroughput = new ProvisionedThroughput(5, 5),
        }));
        refusal.Message.ShouldContain("ProvisionedThroughput must be specified for index: byStatus");

        await client.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = "tbl",
            BillingMode = BillingMode.PROVISIONED,
            ProvisionedThroughput = new ProvisionedThroughput(5, 5),
            GlobalSecondaryIndexUpdates =
            [
                new GlobalSecondaryIndexUpdate { Update = new UpdateGlobalSecondaryIndexAction { IndexName = "byStatus", ProvisionedThroughput = new ProvisionedThroughput(3, 4) } },
            ],
        });
        var index = (await client.DescribeTableAsync("tbl")).Table.GlobalSecondaryIndexes.Single();
        index.ProvisionedThroughput.ReadCapacityUnits.ShouldBe(3);
        index.ProvisionedThroughput.WriteCapacityUnits.ShouldBe(4);
    }

    [Test]
    public async Task UpdateTable_KeepsAKmsKeyGivenAsAnArn()
    {
        var cloud = new InMemoryAwsCloud();
        using var client = await CreateTableAsync(cloud);
        const string KeyArn = "arn:aws:kms:us-east-1:111122223333:key/abc";

        var table = (await client.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = "tbl",
            SSESpecification = new SSESpecification { Enabled = true, SSEType = SSEType.KMS, KMSMasterKeyId = KeyArn },
        })).TableDescription;

        table.SSEDescription.KMSMasterKeyArn.ShouldBe(KeyArn);
    }

    [Test]
    public async Task TimeToLive_LeavesExpiriesMoreThanFiveYearsOld()
    {
        var time = new FakeTimeProvider(Start);
        var cloud = new InMemoryAwsCloud { TimeProvider = time };
        using var client = await CreateTableAsync(cloud);
        await client.UpdateTimeToLiveAsync(new UpdateTimeToLiveRequest
        {
            TableName = "tbl",
            TimeToLiveSpecification = new TimeToLiveSpecification { Enabled = true, AttributeName = "expiresAt" },
        });
        var ancient = Item("ancient", 1);
        ancient["expiresAt"] = new AttributeValue { N = "1" };
        var recent = Item("recent", 1);
        recent["expiresAt"] = new AttributeValue { N = (Start.ToUnixTimeSeconds() - 60).ToString(CultureInfo.InvariantCulture) };
        await client.PutItemAsync("tbl", ancient);
        await client.PutItemAsync("tbl", recent);

        time.Advance(TimeSpan.FromSeconds(5));

        (await client.GetItemAsync("tbl", Item("ancient", 1))).IsItemSet.ShouldBeTrue();
        (await client.GetItemAsync("tbl", Item("recent", 1))).IsItemSet.ShouldBeFalse();
    }

    [Test]
    public async Task ProjectionExpression_NamesTheLeafOfTheDeepestDocument()
    {
        var cloud = new InMemoryAwsCloud();
        using var client = await CreateTableAsync(cloud);
        var document = new AttributeValue("leaf");
        for (var level = 0; level < 31; level++)
        {
            document = new AttributeValue { M = new Dictionary<string, AttributeValue> { ["n"] = document } };
        }

        var item = Item("A", 1);
        item["d"] = document;
        await client.PutItemAsync("tbl", item);

        var path = "d" + string.Concat(Enumerable.Repeat(".n", 31));
        var got = (await client.GetItemAsync(new GetItemRequest { TableName = "tbl", Key = Item("A", 1), ProjectionExpression = path })).Item;
        var value = got["d"];
        for (var level = 0; level < 31; level++)
        {
            value = value.M["n"];
        }

        value.S.ShouldBe("leaf");
        await Should.ThrowAsync<AmazonDynamoDBException>(() => client.GetItemAsync(new GetItemRequest { TableName = "tbl", Key = Item("A", 1), ProjectionExpression = path + ".n" }));
    }

    [Test]
    public async Task Stream_ForgetsRecordsAndDisabledStreamsAfterADay()
    {
        var time = new FakeTimeProvider(Start);
        var cloud = new InMemoryAwsCloud { TimeProvider = time };
        using var client = await CreateTableAsync(cloud, viewType: StreamViewType.NEW_IMAGE);
        using var streams = CreateStreamsClient(cloud);
        var streamArn = (await client.DescribeTableAsync("tbl")).Table.LatestStreamArn;
        await client.PutItemAsync("tbl", Item("old", 1));
        time.Advance(TimeSpan.FromHours(23));
        await client.PutItemAsync("tbl", Item("new", 1));
        time.Advance(TimeSpan.FromHours(2));

        (await ReadAllAsync(streams, streamArn)).Select(r => r.Dynamodb.Keys["pk"].S).ShouldBe(["new"]);

        await client.UpdateTableAsync(new UpdateTableRequest { TableName = "tbl", StreamSpecification = new StreamSpecification { StreamEnabled = false } });
        time.Advance(TimeSpan.FromHours(23));
        (await streams.ListStreamsAsync(new Streams.ListStreamsRequest { TableName = "tbl" })).Streams.Count.ShouldBe(1);
        time.Advance(TimeSpan.FromHours(2));
        ((await streams.ListStreamsAsync(new Streams.ListStreamsRequest { TableName = "tbl" })).Streams ?? []).ShouldBeEmpty();
    }

    private static async Task<AmazonDynamoDBClient> CreateIndexedTableAsync(InMemoryAwsCloud cloud)
    {
        var client = cloud.CreateDynamoDbClient();
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

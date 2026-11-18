using AmazonDynamoDBStreamsClient = Amazon.DynamoDBStreams.AmazonDynamoDBStreamsClient;
using AmazonDynamoDBStreamsConfig = Amazon.DynamoDBStreams.AmazonDynamoDBStreamsConfig;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Streams = Amazon.DynamoDBStreams.Model;

namespace InMemoryAws.Tests.DynamoDb;

/// <summary>
/// DynamoDB Streams and time to live. In AWS SDK v4 the Streams client is its own package, which
/// the library doesn't reference: it reaches the cloud through <c>CreateAwsHttpClientFactory()</c>.
/// Time to live runs on the cloud's <see cref="TimeProvider"/>.
/// </summary>
public sealed class DynamoDbStreamsAndTimeToLiveTests
{
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private static async Task<AmazonDynamoDBClient> CreateTableAsync(InMemoryAwsCloud cloud, StreamViewType? viewType = null)
    {
        var client = cloud.CreateDynamoDbClient();
        await client.CreateTableAsync(new CreateTableRequest
        {
            TableName = "events",
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            StreamSpecification = viewType is null ? null : new StreamSpecification { StreamEnabled = true, StreamViewType = viewType },
        });
        return client;
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
    public async Task Stream_RecordsEveryWriteInOrderWithBothImages()
    {
        var cloud = new InMemoryAwsCloud();
        using var client = await CreateTableAsync(cloud, StreamViewType.NEW_AND_OLD_IMAGES);
        using var streams = CreateStreamsClient(cloud);

        await client.PutItemAsync("events", new Dictionary<string, AttributeValue> { ["pk"] = new("a"), ["v"] = new("1") });
        await client.PutItemAsync("events", new Dictionary<string, AttributeValue> { ["pk"] = new("a"), ["v"] = new("2") });
        await client.DeleteItemAsync("events", new Dictionary<string, AttributeValue> { ["pk"] = new("a") });

        var streamArn = (await client.DescribeTableAsync("events")).Table.LatestStreamArn;
        var records = await ReadAllAsync(streams, streamArn);

        records.Select(r => r.EventName.Value).ShouldBe(["INSERT", "MODIFY", "REMOVE"]);
        records[1].Dynamodb.OldImage["v"].S.ShouldBe("1");
        records[1].Dynamodb.NewImage["v"].S.ShouldBe("2");
        records[2].Dynamodb.Keys["pk"].S.ShouldBe("a");
        string.CompareOrdinal(records[0].Dynamodb.SequenceNumber, records[1].Dynamodb.SequenceNumber).ShouldBeLessThan(0);
    }

    [Test]
    public async Task TimeToLive_DeletesExpiredItemsAsTimeAdvances()
    {
        var time = new FakeTimeProvider(Start);
        var cloud = new InMemoryAwsCloud { TimeProvider = time };
        using var client = await CreateTableAsync(cloud, StreamViewType.KEYS_ONLY);
        using var streams = CreateStreamsClient(cloud);
        await client.UpdateTimeToLiveAsync(new UpdateTimeToLiveRequest
        {
            TableName = "events",
            TimeToLiveSpecification = new TimeToLiveSpecification { Enabled = true, AttributeName = "expiresAt" },
        });

        var soon = Start.AddMinutes(5).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var later = Start.AddHours(5).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        await client.PutItemAsync("events", new Dictionary<string, AttributeValue> { ["pk"] = new("soon"), ["expiresAt"] = new() { N = soon } });
        await client.PutItemAsync("events", new Dictionary<string, AttributeValue> { ["pk"] = new("later"), ["expiresAt"] = new() { N = later } });

        time.Advance(TimeSpan.FromMinutes(10));

        var remaining = (await client.ScanAsync(new ScanRequest { TableName = "events" })).Items.Select(i => i["pk"].S);
        remaining.ShouldBe(["later"]);

        var streamArn = (await client.DescribeTableAsync("events")).Table.LatestStreamArn;
        var removal = (await ReadAllAsync(streams, streamArn)).Single(r => r.EventName == Amazon.DynamoDBStreams.OperationType.REMOVE);
        removal.UserIdentity.PrincipalId.ShouldBe("dynamodb.amazonaws.com");
        removal.UserIdentity.Type.ShouldBe("Service");
    }

    [Test]
    public async Task Tables_AreRegional()
    {
        var cloud = new InMemoryAwsCloud { CurrentRegion = "eu-west-1" };
        using var client = await CreateTableAsync(cloud);
        using var otherRegion = new AmazonDynamoDBClient(new AnonymousAWSCredentials(), new AmazonDynamoDBConfig
        {
            ServiceURL = "https://dynamodb.us-east-1.amazonaws.com",
            AuthenticationRegion = "us-east-1",
            MaxErrorRetry = 0,
            HttpClientFactory = cloud.CreateAwsHttpClientFactory(),
        });

        (await client.ListTablesAsync(new ListTablesRequest())).TableNames.ShouldBe(["events"]);
        (await otherRegion.ListTablesAsync(new ListTablesRequest())).TableNames.ShouldBeEmpty();
    }
}

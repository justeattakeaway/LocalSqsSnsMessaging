using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.EventBridge.Model;
using Amazon.S3.Model;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Shouldly;
#if AWS_SDK_V3
using StreamsModel = Amazon.DynamoDBv2.Model;
#else
using StreamsModel = Amazon.DynamoDBStreams.Model;
#endif
using Target = Amazon.EventBridge.Model.Target;

namespace InMemoryAws.Tests.BringYourOwnSdk;

/// <summary>
/// The clients the InMemoryAws package's build integration adds, compiled against the AWS SDK the project
/// brings: AWS SDK v3 on .NET and .NET Framework (InMemoryAws.Tests.AwsSdkV3), and AWS SDK v4 on .NET Framework
/// (InMemoryAws.Tests.NetFramework). On .NET Framework they go through the SDK's request pipeline, as the SDK has
/// no HttpClientFactory there. (AWS SDK v4 on .NET is the rest of the test suite.)
/// </summary>
public sealed class BringYourOwnSdkTests
{
    [Test]
    public void TheProjectIsOnTheIntendedAwsSdk()
    {
#if AWS_SDK_V3
        typeof(AmazonSQSClient).Assembly.GetName().Version!.Major.ShouldBe(3);
#else
        typeof(AmazonSQSClient).Assembly.GetName().Version!.Major.ShouldBe(4);
#endif
    }

    [Test]
    public async Task SqsSendsAndReceives()
    {
        using var sqs = new InMemoryAwsCloud().CreateSqsClient();

        var queueUrl = (await sqs.CreateQueueAsync("orders")).QueueUrl;
        await sqs.SendMessageAsync(queueUrl, "hello");
        var received = await sqs.ReceiveMessageAsync(queueUrl);

        received.Messages.ShouldHaveSingleItem().Body.ShouldBe("hello");
    }

    [Test]
    public async Task AnErrorResponseBecomesTheSdksException()
    {
        using var sqs = new InMemoryAwsCloud().CreateSqsClient();

        var error = await Should.ThrowAsync<QueueDoesNotExistException>(() => sqs.GetQueueUrlAsync("missing"));

        error.StatusCode.ShouldBe(System.Net.HttpStatusCode.BadRequest);
    }

#if NETFRAMEWORK
    [Test]
    public void TheSynchronousApiWorks()
    {
        using var sqs = new InMemoryAwsCloud().CreateSqsClient();

        var queueUrl = sqs.CreateQueue("orders").QueueUrl;
        sqs.SendMessage(queueUrl, "hello");

        sqs.ReceiveMessage(queueUrl).Messages.ShouldHaveSingleItem().Body.ShouldBe("hello");
        Should.Throw<QueueDoesNotExistException>(() => sqs.GetQueueUrl("missing"));
    }
#endif

    [Test]
    public async Task SnsDeliversToASubscribedQueue()
    {
        var cloud = new InMemoryAwsCloud();
        using var sqs = cloud.CreateSqsClient();
        using var sns = cloud.CreateSnsClient();
        var queueUrl = (await sqs.CreateQueueAsync("orders")).QueueUrl;
        var queueArn = (await sqs.GetQueueAttributesAsync(queueUrl, ["QueueArn"])).Attributes["QueueArn"];
        var topicArn = (await sns.CreateTopicAsync("orders")).TopicArn;
        await sns.SubscribeAsync(new SubscribeRequest(topicArn, "sqs", queueArn)
        {
            Attributes = new Dictionary<string, string> { ["RawMessageDelivery"] = "true" },
        });

        await sns.PublishAsync(topicArn, "placed");

        (await sqs.ReceiveMessageAsync(queueUrl)).Messages.ShouldHaveSingleItem().Body.ShouldBe("placed");
    }

    [Test]
    public async Task EventBridgeRoutesToAQueueTarget()
    {
        var cloud = new InMemoryAwsCloud();
        using var sqs = cloud.CreateSqsClient();
        using var events = cloud.CreateEventBridgeClient();
        var queueUrl = (await sqs.CreateQueueAsync("orders")).QueueUrl;
        var queueArn = (await sqs.GetQueueAttributesAsync(queueUrl, ["QueueArn"])).Attributes["QueueArn"];
        await events.PutRuleAsync(new PutRuleRequest { Name = "orders", EventPattern = """{"source":["shop"]}""" });
        await events.PutTargetsAsync(new PutTargetsRequest { Rule = "orders", Targets = [new Target { Id = "q", Arn = queueArn }] });

        var put = await events.PutEventsAsync(new PutEventsRequest
        {
            Entries = [new PutEventsRequestEntry { Source = "shop", DetailType = "OrderPlaced", Detail = """{"id":1}""" }],
        });

        put.FailedEntryCount.ShouldBe(0);
        (await sqs.ReceiveMessageAsync(queueUrl)).Messages.ShouldHaveSingleItem().Body.ShouldContain("\"OrderPlaced\"");
    }

    [Test]
    public async Task S3StoresAndListsObjects()
    {
        using var s3 = new InMemoryAwsCloud().CreateS3Client();
        await s3.PutBucketAsync("orders");
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = "orders", Key = "2024/one.json", ContentBody = "{\"id\":1}" });

        using (var response = await s3.GetObjectAsync("orders", "2024/one.json"))
        using (var reader = new StreamReader(response.ResponseStream))
        {
            (await reader.ReadToEndAsync()).ShouldBe("{\"id\":1}");
        }

        var listed = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = "orders", Prefix = "2024/" });
        listed.S3Objects.ShouldHaveSingleItem().Key.ShouldBe("2024/one.json");
        var missing = await Should.ThrowAsync<Amazon.S3.AmazonS3Exception>(() => s3.GetObjectAsync("orders", "missing"));
        missing.ErrorCode.ShouldBe("NoSuchKey");
    }

    [Test]
    public async Task DynamoDbWritesAppearOnTheTablesStream()
    {
        var cloud = new InMemoryAwsCloud();
        using var dynamoDb = cloud.CreateDynamoDbClient();
        using var streams = cloud.CreateDynamoDbStreamsClient();
        var table = await dynamoDb.CreateTableAsync(new CreateTableRequest
        {
            TableName = "orders",
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            StreamSpecification = new StreamSpecification { StreamEnabled = true, StreamViewType = StreamViewType.NEW_IMAGE },
        });

        await dynamoDb.PutItemAsync("orders", new Dictionary<string, AttributeValue> { ["pk"] = new("a") });
        (await dynamoDb.GetItemAsync("orders", new Dictionary<string, AttributeValue> { ["pk"] = new("a") })).Item["pk"].S.ShouldBe("a");

        var streamArn = table.TableDescription.LatestStreamArn;
        var shard = (await streams.DescribeStreamAsync(new StreamsModel.DescribeStreamRequest { StreamArn = streamArn }))
            .StreamDescription.Shards.ShouldHaveSingleItem();
        var iterator = (await streams.GetShardIteratorAsync(new StreamsModel.GetShardIteratorRequest
        {
            StreamArn = streamArn,
            ShardId = shard.ShardId,
            ShardIteratorType = "TRIM_HORIZON",
        })).ShardIterator;
        var records = await streams.GetRecordsAsync(new StreamsModel.GetRecordsRequest { ShardIterator = iterator });

        records.Records.ShouldHaveSingleItem().Dynamodb.NewImage["pk"].S.ShouldBe("a");
    }

#if !NETFRAMEWORK
    [Test]
    public async Task TheSdkHttpClientFactoryRoutesClientsConfiguredElsewhere()
    {
        var cloud = new InMemoryAwsCloud();
        using var factory = cloud.CreateAwsSdkHttpClientFactory();
        using var sqs = new AmazonSQSClient(
            new Amazon.Runtime.BasicAWSCredentials("test", "test"),
            new AmazonSQSConfig { RegionEndpoint = Amazon.RegionEndpoint.EUWest1, HttpClientFactory = factory });

        var queueUrl = (await sqs.CreateQueueAsync("orders")).QueueUrl;

        queueUrl.ShouldContain("eu-west-1");
        (await sqs.ListQueuesAsync(new ListQueuesRequest())).QueueUrls.ShouldBe([queueUrl]);
        using var usEast1 = cloud.CreateSqsClient();
        ((await usEast1.ListQueuesAsync(new ListQueuesRequest())).QueueUrls ?? []).ShouldBeEmpty();
    }
#endif
}

using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.EventBridge.Model;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Shouldly;
using Target = Amazon.EventBridge.Model.Target;

namespace InMemoryAws.Tests.AwsSdkV3;

/// <summary>
/// The clients the InMemoryAws package wires up for a project on AWS SDK v3, compiled against that SDK
/// (including the DynamoDB Streams client, which v3 keeps in AWSSDK.DynamoDBv2).
/// </summary>
public sealed class AwsSdkV3Tests
{
    [Test]
    public void TheProjectIsOnAwsSdkV3()
    {
        typeof(AmazonSQSClient).Assembly.GetName().Version!.Major.ShouldBe(3);
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
        var shard = (await streams.DescribeStreamAsync(new DescribeStreamRequest { StreamArn = streamArn })).StreamDescription.Shards.ShouldHaveSingleItem();
        var iterator = (await streams.GetShardIteratorAsync(new GetShardIteratorRequest
        {
            StreamArn = streamArn,
            ShardId = shard.ShardId,
            ShardIteratorType = ShardIteratorType.TRIM_HORIZON,
        })).ShardIterator;
        var records = await streams.GetRecordsAsync(new GetRecordsRequest { ShardIterator = iterator });

        records.Records.ShouldHaveSingleItem().Dynamodb.NewImage["pk"].S.ShouldBe("a");
    }

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
        (await usEast1.ListQueuesAsync(new ListQueuesRequest())).QueueUrls.ShouldBeEmpty();
    }
}

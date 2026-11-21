using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.EventBridge.Model;
using Amazon.S3.Model;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using Shouldly;
using ListStreamsRequest = Amazon.DynamoDBStreams.Model.ListStreamsRequest;

namespace InMemoryAws.Tests.SdkClient;

/// <summary>
/// The <c>Create...Client(region, configure)</c> overloads the build integration adds: the client is in the region
/// it names, so it is served that region's resources, and its ARNs and URLs name it.
/// </summary>
public sealed class CreateClientInRegionTests
{
    private const string Region = "eu-west-1";

    [Test]
    public async Task AnSqsClientIsServedItsRegionsQueues()
    {
        var cloud = new InMemoryAwsCloud();
        using var sqs = cloud.CreateSqsClient(Region);
        using var usEast1 = cloud.CreateSqsClient();

        var queueUrl = (await sqs.CreateQueueAsync("orders")).QueueUrl;
        var queueArn = (await sqs.GetQueueAttributesAsync(queueUrl, ["QueueArn"])).QueueARN;

        queueUrl.ShouldBe("https://sqs.eu-west-1.amazonaws.com/000000000000/orders");
        queueArn.ShouldBe("arn:aws:sqs:eu-west-1:000000000000:orders");
        await Should.ThrowAsync<QueueDoesNotExistException>(() => usEast1.GetQueueUrlAsync("orders"));
    }

    [Test]
    public async Task AnSnsClientIsServedItsRegionsTopics()
    {
        var cloud = new InMemoryAwsCloud();
        using var sns = cloud.CreateSnsClient(Region);
        using var usEast1 = cloud.CreateSnsClient();

        var topicArn = (await sns.CreateTopicAsync("orders")).TopicArn;

        topicArn.ShouldBe("arn:aws:sns:eu-west-1:000000000000:orders");
        (await sns.ListTopicsAsync(new ListTopicsRequest())).Topics.ShouldHaveSingleItem().TopicArn.ShouldBe(topicArn);
        ((await usEast1.ListTopicsAsync(new ListTopicsRequest())).Topics ?? []).ShouldBeEmpty();
    }

    [Test]
    public async Task SnsInARegionDeliversToAQueueInThatRegion()
    {
        var cloud = new InMemoryAwsCloud();
        using var sqs = cloud.CreateSqsClient(Region);
        using var sns = cloud.CreateSnsClient(Region);
        var queueUrl = (await sqs.CreateQueueAsync("orders")).QueueUrl;
        var queueArn = (await sqs.GetQueueAttributesAsync(queueUrl, ["QueueArn"])).QueueARN;
        var topicArn = (await sns.CreateTopicAsync("orders")).TopicArn;
        await sns.SubscribeAsync(new SubscribeRequest(topicArn, "sqs", queueArn)
        {
            Attributes = new Dictionary<string, string> { ["RawMessageDelivery"] = "true" },
        });

        await sns.PublishAsync(topicArn, "placed");

        (await sqs.ReceiveMessageAsync(queueUrl)).Messages.ShouldHaveSingleItem().Body.ShouldBe("placed");
    }

    [Test]
    public async Task AnEventBridgeClientIsServedItsRegionsEventBuses()
    {
        var cloud = new InMemoryAwsCloud();
        using var events = cloud.CreateEventBridgeClient(Region);
        using var usEast1 = cloud.CreateEventBridgeClient();

        var busArn = (await events.CreateEventBusAsync(new CreateEventBusRequest { Name = "orders" })).EventBusArn;

        busArn.ShouldBe("arn:aws:events:eu-west-1:000000000000:event-bus/orders");
        await Should.ThrowAsync<Amazon.EventBridge.Model.ResourceNotFoundException>(
            () => usEast1.DescribeEventBusAsync(new DescribeEventBusRequest { Name = "orders" }));
    }

    [Test]
    public async Task DynamoDbAndStreamsClientsAreServedTheirRegionsTables()
    {
        var cloud = new InMemoryAwsCloud();
        using var dynamoDb = cloud.CreateDynamoDbClient(Region);
        using var streams = cloud.CreateDynamoDbStreamsClient(Region);
        using var usEast1 = cloud.CreateDynamoDbClient();
        using var usEast1Streams = cloud.CreateDynamoDbStreamsClient();

        var table = (await dynamoDb.CreateTableAsync(new CreateTableRequest
        {
            TableName = "orders",
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            StreamSpecification = new StreamSpecification { StreamEnabled = true, StreamViewType = StreamViewType.NEW_IMAGE },
        })).TableDescription;

        table.TableArn.ShouldBe("arn:aws:dynamodb:eu-west-1:000000000000:table/orders");
        table.LatestStreamArn.ShouldStartWith("arn:aws:dynamodb:eu-west-1:000000000000:table/orders/stream/");
        (await streams.ListStreamsAsync(new ListStreamsRequest())).Streams.ShouldHaveSingleItem().StreamArn.ShouldBe(table.LatestStreamArn);
        await Should.ThrowAsync<Amazon.DynamoDBv2.Model.ResourceNotFoundException>(() => usEast1.DescribeTableAsync("orders"));
        ((await usEast1Streams.ListStreamsAsync(new ListStreamsRequest())).Streams ?? []).ShouldBeEmpty();
    }

    [Test]
    public async Task AnS3ClientCreatesBucketsInItsRegion()
    {
        var cloud = new InMemoryAwsCloud();
        const string region = "eu-west-2";
        using var s3 = cloud.CreateS3Client(region);

        // The SDK sends the client's region as the location constraint, which S3 wants outside us-east-1.
        await s3.PutBucketAsync("orders");

        (await s3.GetBucketLocationAsync("orders")).Location.Value.ShouldBe(region);
        ((Amazon.S3.AmazonS3Config)s3.Config).ForcePathStyle.ShouldBeTrue();
    }

    [Test]
    public async Task AnS3ClientInEuWest1CreatesBucketsThere()
    {
        // The .NET SDK sends eu-west-1 as the legacy "EU" location constraint, which S3 takes there.
        var cloud = new InMemoryAwsCloud();
        using var s3 = cloud.CreateS3Client(Region);

        await s3.PutBucketAsync("orders");
        await s3.PutBucketAsync(new PutBucketRequest { BucketName = "invoices", BucketRegionName = Region });

        (await s3.GetBucketLocationAsync("orders")).Location.Value.ShouldBe(Region);
        (await s3.GetBucketLocationAsync("invoices")).Location.Value.ShouldBe(Region);
    }

    [Test]
    public async Task NoRegionIsTheCloudsCurrentRegion()
    {
        var cloud = new InMemoryAwsCloud { CurrentRegion = "ap-southeast-2" };
        using var sqs = cloud.CreateSqsClient(null!);

        (await sqs.CreateQueueAsync("orders")).QueueUrl.ShouldBe("https://sqs.ap-southeast-2.amazonaws.com/000000000000/orders");
        sqs.Config.AuthenticationRegion.ShouldBe("ap-southeast-2");
    }

    [Test]
    [Arguments("EU-WEST-1")]
    [Arguments("eu_west_1")]
    [Arguments("localhost")]
    [Arguments("")]
    public void ARegionThatIsNotARegionNameIsRefused(string region)
    {
        // The cloud would otherwise read no region from the endpoint host and quietly serve CurrentRegion.
        var cloud = new InMemoryAwsCloud();

        Should.Throw<ArgumentException>(() => cloud.CreateSqsClient(region)).ParamName.ShouldBe("region");
        Should.Throw<ArgumentException>(() => cloud.CreateS3Client(region)).ParamName.ShouldBe("region");
    }

    [Test]
    public async Task ConfigureChangesTheConfigOnceItPointsAtTheCloud()
    {
        var cloud = new InMemoryAwsCloud();
        var seen = (ServiceUrl: (string?)null, Retries: -1);
        using var sqs = cloud.CreateSqsClient(Region, config =>
        {
            seen = (config.ServiceURL, config.MaxErrorRetry);
            config.Timeout = TimeSpan.FromSeconds(7);
        });

        seen.ShouldBe(("https://sqs.eu-west-1.amazonaws.com/", 0));
        sqs.Config.Timeout.ShouldBe(TimeSpan.FromSeconds(7));
        (await sqs.CreateQueueAsync("orders")).QueueUrl.ShouldContain(Region);
    }
}

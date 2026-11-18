using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Shouldly;

namespace InMemoryAws.Tests.SdkClient;

/// <summary>
/// Smoke tests for the SDK SNS client using HttpMessageHandler.
/// </summary>
public sealed class SnsSdkClientSmokeTests
{
    [Test]
    public async Task CreateTopic_WithSdkClient_ShouldSucceed()
    {
        // Arrange
        var cloud = new InMemoryAwsCloud();
        using var sns = cloud.CreateSnsClient();

        // Act
        var response = await sns.CreateTopicAsync("test-topic");

        // Assert
        response.ShouldNotBeNull();
        response.TopicArn.ShouldContain("test-topic");
    }

    [Test]
    public async Task PublishToTopic_WithSdkClient_ShouldSucceed()
    {
        // Arrange
        var cloud = new InMemoryAwsCloud();
        using var sns = cloud.CreateSnsClient();
        using var sqs = cloud.CreateSqsClient();

        var topicArn = (await sns.CreateTopicAsync("test-topic")).TopicArn;
        var queueUrl = (await sqs.CreateQueueAsync("test-queue")).QueueUrl;
        var queueArn = (await sqs.GetQueueAttributesAsync(queueUrl, ["QueueArn"])).Attributes["QueueArn"];

        // Subscribe queue to topic
        await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "sqs",
            Endpoint = queueArn,
            Attributes = new Dictionary<string, string> { ["RawMessageDelivery"] = "true" }
        });

        // Act
        await sns.PublishAsync(topicArn, "Hello from SDK SNS client!");

        var receiveResponse = await sqs.ReceiveMessageAsync(queueUrl);

        // Assert
        receiveResponse.Messages.ShouldHaveSingleItem();
        receiveResponse.Messages[0].Body.ShouldBe("Hello from SDK SNS client!");
    }

    [Test]
    public async Task GetTopicAttributes_WithSdkClient_ShouldSucceed()
    {
        // Arrange
        var cloud = new InMemoryAwsCloud();
        using var sns = cloud.CreateSnsClient();
        var topicArn = (await sns.CreateTopicAsync("test-topic")).TopicArn;

        // Act
        var response = await sns.GetTopicAttributesAsync(topicArn);

        // Assert
        response.Attributes.ShouldContainKey("TopicArn");
        response.Attributes["TopicArn"].ShouldBe(topicArn);
    }
}

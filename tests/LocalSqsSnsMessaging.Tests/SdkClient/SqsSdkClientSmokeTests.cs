using Amazon.SQS;
using Amazon.SQS.Model;
using Shouldly;

namespace LocalSqsSnsMessaging.Tests.SdkClient;

/// <summary>
/// Smoke tests for the SDK SQS client using HttpMessageHandler.
/// </summary>
public sealed class SqsSdkClientSmokeTests
{
    [Test]
    public async Task CreateQueue_WithSdkClient_ShouldSucceed()
    {
        // Arrange
        var bus = new InMemoryAwsBus();
        using var sqs = bus.CreateSqsClient();

        // Act
        var response = await sqs.CreateQueueAsync("test-queue");

        // Assert
        response.ShouldNotBeNull();
        response.QueueUrl.ShouldContain("test-queue");
    }

    [Test]
    public async Task SendAndReceiveMessage_WithSdkClient_ShouldSucceed()
    {
        // Arrange
        var bus = new InMemoryAwsBus();
        using var sqs = bus.CreateSqsClient();
        var queueUrl = (await sqs.CreateQueueAsync("test-queue")).QueueUrl;

        // Act
        await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "Hello from SDK client!"
        });

        var receiveResponse = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 1
        });

        // Assert
        receiveResponse.Messages.ShouldHaveSingleItem();
        receiveResponse.Messages[0].Body.ShouldBe("Hello from SDK client!");
    }

    [Test]
    public async Task GetQueueAttributes_WithSdkClient_ShouldSucceed()
    {
        // Arrange
        var bus = new InMemoryAwsBus();
        using var sqs = bus.CreateSqsClient();
        var queueUrl = (await sqs.CreateQueueAsync("test-queue")).QueueUrl;

        // Act
        var response = await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            AttributeNames = ["QueueArn"]
        });

        // Assert
        response.Attributes.ShouldContainKey("QueueArn");
        response.Attributes["QueueArn"].ShouldContain("test-queue");
    }
}

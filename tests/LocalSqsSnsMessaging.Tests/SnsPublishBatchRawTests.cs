using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using Shouldly;

namespace LocalSqsSnsMessaging.Tests;

public class SnsPublishBatchRawTests
{
    [Test]
    public async Task PublishBatchAsync_RawClient_ShouldReturnSuccessfulEntries()
    {
        // Arrange
        var bus = new InMemoryAwsBus();
        using var sns = bus.CreateRawSnsClient();
        using var sqs = bus.CreateRawSqsClient();

        var topicName = "TestTopic";
        var queueName = "TestQueue";
        await sns.CreateTopicAsync(new CreateTopicRequest { Name = topicName });
        await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = queueName });

        var topicArn = $"arn:aws:sns:us-east-1:{bus.CurrentAccountId}:{topicName}";
        var queueArn = $"arn:aws:sqs:us-east-1:{bus.CurrentAccountId}:{queueName}";

        await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "sqs",
            Endpoint = queueArn,
            Attributes = new Dictionary<string, string> { ["RawMessageDelivery"] = "true" }
        });

        var request = new PublishBatchRequest
        {
            TopicArn = topicArn,
            PublishBatchRequestEntries = new List<PublishBatchRequestEntry>
            {
                new PublishBatchRequestEntry { Id = "1", Message = "Test 1" },
                new PublishBatchRequestEntry { Id = "2", Message = "Test 2" }
            }
        };

        // Act
        var response = await sns.PublishBatchAsync(request);

        // Assert
        response.ShouldNotBeNull();
        response.Successful.ShouldNotBeNull();
        response.Failed.ShouldNotBeNull();
        response.Successful.Count.ShouldBe(2);
        response.Failed.Count.ShouldBe(0);
        response.Successful[0].Id.ShouldBe("1");
        response.Successful[0].MessageId.ShouldNotBeNullOrEmpty();
        response.Successful[1].Id.ShouldBe("2");
        response.Successful[1].MessageId.ShouldNotBeNullOrEmpty();
    }
}

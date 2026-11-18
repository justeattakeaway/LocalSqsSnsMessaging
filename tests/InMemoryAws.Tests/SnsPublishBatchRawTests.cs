using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using Shouldly;

namespace InMemoryAws.Tests;

public class SnsPublishBatchRawTests
{
    [Test]
    public async Task PublishBatchAsync_ShouldReturnSuccessfulEntries()
    {
        // Arrange
        var cloud = new InMemoryAwsCloud();
        using var sns = cloud.CreateSnsClient();
        using var sqs = cloud.CreateSqsClient();

        var topicName = "TestTopic";
        var queueName = "TestQueue";
        await sns.CreateTopicAsync(new CreateTopicRequest { Name = topicName });
        await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = queueName });

        var topicArn = $"arn:aws:sns:us-east-1:{cloud.CurrentAccountId}:{topicName}";
        var queueArn = $"arn:aws:sqs:us-east-1:{cloud.CurrentAccountId}:{queueName}";

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
        response.Successful.Count.ShouldBe(2);
        response.Failed.ShouldBeEmptyAwsCollection();
        response.Successful[0].Id.ShouldBe("1");
        response.Successful[0].MessageId.ShouldNotBeNullOrEmpty();
        response.Successful[1].Id.ShouldBe("2");
        response.Successful[1].MessageId.ShouldNotBeNullOrEmpty();
    }
}

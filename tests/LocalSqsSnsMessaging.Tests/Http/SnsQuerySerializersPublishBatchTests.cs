using Amazon.SimpleNotificationService.Model;
using LocalSqsSnsMessaging.Http.Handlers;
using Shouldly;

namespace LocalSqsSnsMessaging.Tests.Http;

public class SnsQuerySerializersPublishBatchTests
{
    [Test]
    public void DeserializePublishBatchRequest_WithTwoEntries_ShouldParseCorrectly()
    {
        // Arrange
        var requestBody = "Action=PublishBatch&TopicArn=arn:aws:sns:us-east-1:123456789012:TestTopic" +
                          "&PublishBatchRequestEntries.member.1.Id=1&PublishBatchRequestEntries.member.1.Message=Test1" +
                          "&PublishBatchRequestEntries.member.2.Id=2&PublishBatchRequestEntries.member.2.Message=Test2";

        // Act
        var request = SnsQuerySerializers.DeserializePublishBatchRequest(requestBody);

        // Assert
        request.ShouldNotBeNull();
        request.TopicArn.ShouldBe("arn:aws:sns:us-east-1:123456789012:TestTopic");
        request.PublishBatchRequestEntries.ShouldNotBeNull();
        request.PublishBatchRequestEntries.Count.ShouldBe(2);
        request.PublishBatchRequestEntries[0].Id.ShouldBe("1");
        request.PublishBatchRequestEntries[0].Message.ShouldBe("Test1");
        request.PublishBatchRequestEntries[1].Id.ShouldBe("2");
        request.PublishBatchRequestEntries[1].Message.ShouldBe("Test2");
    }
}

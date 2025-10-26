using System.Buffers;
using System.Text;
using System.Xml.Linq;
using Amazon.SimpleNotificationService.Model;
using LocalSqsSnsMessaging.Http.Handlers;
using Shouldly;

namespace LocalSqsSnsMessaging.Tests.Http;

/// <summary>
/// Acceptance tests for SnsQuerySerializers to verify request deserialization and response serialization
/// work correctly without requiring the full InMemoryAwsBus infrastructure.
/// </summary>
public sealed class SnsQuerySerializersTests
{
    [Test]
    public void DeserializePublishRequest_WithBasicMessage_ShouldParseCorrectly()
    {
        // Arrange
        var queryString = "TopicArn=arn:aws:sns:us-east-1:123456789012:my-topic&Message=Hello%2C+World!";

        // Act
        var request = SnsQuerySerializers.DeserializePublishRequest(Encoding.UTF8.GetBytes(queryString));

        // Assert
        request.TopicArn.ShouldBe("arn:aws:sns:us-east-1:123456789012:my-topic");
        request.Message.ShouldBe("Hello, World!");
    }

    [Test]
    public void DeserializePublishRequest_WithMessageAttributes_ShouldParseCorrectly()
    {
        // Arrange - AWS SDK sends message attributes using indexed notation
        var queryString = "TopicArn=arn:aws:sns:us-east-1:123456789012:my-topic" +
                          "&Message=Test+message" +
                          "&MessageAttributes.entry.1.Name=CustomerId" +
                          "&MessageAttributes.entry.1.Value.DataType=String" +
                          "&MessageAttributes.entry.1.Value.StringValue=12345" +
                          "&MessageAttributes.entry.2.Name=Priority" +
                          "&MessageAttributes.entry.2.Value.DataType=Number" +
                          "&MessageAttributes.entry.2.Value.StringValue=1";

        // Act
        var request = SnsQuerySerializers.DeserializePublishRequest(Encoding.UTF8.GetBytes(queryString));

        // Assert
        request.TopicArn.ShouldBe("arn:aws:sns:us-east-1:123456789012:my-topic");
        request.Message.ShouldBe("Test message");
        request.MessageAttributes.ShouldNotBeNull();
        request.MessageAttributes.ShouldContainKey("CustomerId");
        request.MessageAttributes["CustomerId"].StringValue.ShouldBe("12345");
        request.MessageAttributes["CustomerId"].DataType.ShouldBe("String");
        request.MessageAttributes.ShouldContainKey("Priority");
        request.MessageAttributes["Priority"].StringValue.ShouldBe("1");
        request.MessageAttributes["Priority"].DataType.ShouldBe("Number");
    }

    [Test]
    public void SerializePublishResponse_ShouldProduceValidXml()
    {
        // Arrange
        var response = new PublishResponse
        {
            MessageId = "msg-12345-abcde",
            SequenceNumber = "seq-67890"
        };
        var buffer = new ArrayBufferWriter<byte>();

        // Act
        SnsQuerySerializers.SerializePublishResponse(response, buffer);

        // Assert
        var xml = Encoding.UTF8.GetString(buffer.WrittenSpan);
        var doc = XDocument.Parse(xml);

        var ns = XNamespace.Get("http://sns.amazonaws.com/doc/2010-03-31/");
        var root = doc.Root;
        root.ShouldNotBeNull();
        root!.Name.ShouldBe(ns + "PublishResponse");

        var result = root.Element(ns + "PublishResult");
        result.ShouldNotBeNull();
        result!.Element(ns + "MessageId")?.Value.ShouldBe("msg-12345-abcde");
        result.Element(ns + "SequenceNumber")?.Value.ShouldBe("seq-67890");

        var metadata = root.Element(ns + "ResponseMetadata");
        metadata.ShouldNotBeNull();
        metadata!.Element(ns + "RequestId")?.Value.ShouldNotBeNullOrEmpty();
    }

    [Test]
    public void DeserializeCreateTopicRequest_ShouldParseCorrectly()
    {
        // Arrange
        var queryString = "Name=my-new-topic&Attributes.entry.1.key=FifoTopic&Attributes.entry.1.value=true";

        // Act
        var request = SnsQuerySerializers.DeserializeCreateTopicRequest(Encoding.UTF8.GetBytes(queryString));

        // Assert
        request.Name.ShouldBe("my-new-topic");
        request.Attributes.ShouldNotBeNull();
        request.Attributes.ShouldContainKey("FifoTopic");
        request.Attributes["FifoTopic"].ShouldBe("true");
    }

    [Test]
    public void SerializeCreateTopicResponse_ShouldProduceValidXml()
    {
        // Arrange
        var response = new CreateTopicResponse
        {
            TopicArn = "arn:aws:sns:us-east-1:123456789012:my-new-topic"
        };
        var buffer = new ArrayBufferWriter<byte>();

        // Act
        SnsQuerySerializers.SerializeCreateTopicResponse(response, buffer);

        // Assert
        buffer.WrittenCount.ShouldBeGreaterThan(0, "Buffer should have data written to it");
        var xml = Encoding.UTF8.GetString(buffer.WrittenSpan);
        xml.ShouldNotBeNullOrWhiteSpace("XML should not be empty");
        var doc = XDocument.Parse(xml);

        var ns = XNamespace.Get("http://sns.amazonaws.com/doc/2010-03-31/");
        var root = doc.Root;
        root.ShouldNotBeNull();
        root!.Name.ShouldBe(ns + "CreateTopicResponse");

        var result = root.Element(ns + "CreateTopicResult");
        result.ShouldNotBeNull();
        result!.Element(ns + "TopicArn")?.Value.ShouldBe("arn:aws:sns:us-east-1:123456789012:my-new-topic");
    }

    [Test]
    public void DeserializeSubscribeRequest_ShouldParseCorrectly()
    {
        // Arrange
        var queryString = "TopicArn=arn:aws:sns:us-east-1:123456789012:my-topic" +
                          "&Protocol=sqs" +
                          "&Endpoint=arn:aws:sqs:us-east-1:123456789012:my-queue" +
                          "&ReturnSubscriptionArn=true";

        // Act
        var request = SnsQuerySerializers.DeserializeSubscribeRequest(Encoding.UTF8.GetBytes(queryString));

        // Assert
        request.TopicArn.ShouldBe("arn:aws:sns:us-east-1:123456789012:my-topic");
        request.Protocol.ShouldBe("sqs");
        request.Endpoint.ShouldBe("arn:aws:sqs:us-east-1:123456789012:my-queue");
        request.ReturnSubscriptionArn.ShouldBe(true);
    }

    [Test]
    public void SerializeSubscribeResponse_ShouldProduceValidXml()
    {
        // Arrange
        var response = new SubscribeResponse
        {
            SubscriptionArn = "arn:aws:sns:us-east-1:123456789012:my-topic:12345678-1234-1234-1234-123456789012"
        };
        var buffer = new ArrayBufferWriter<byte>();

        // Act
        SnsQuerySerializers.SerializeSubscribeResponse(response, buffer);

        // Assert
        var xml = Encoding.UTF8.GetString(buffer.WrittenSpan);
        var doc = XDocument.Parse(xml);

        var ns = XNamespace.Get("http://sns.amazonaws.com/doc/2010-03-31/");
        var root = doc.Root;
        root.ShouldNotBeNull();
        root!.Name.ShouldBe(ns + "SubscribeResponse");

        var result = root.Element(ns + "SubscribeResult");
        result.ShouldNotBeNull();
        result!.Element(ns + "SubscriptionArn")?.Value.ShouldBe("arn:aws:sns:us-east-1:123456789012:my-topic:12345678-1234-1234-1234-123456789012");
    }

    [Test]
    public void DeserializeDeleteTopicRequest_ShouldParseCorrectly()
    {
        // Arrange
        var queryString = "TopicArn=arn:aws:sns:us-east-1:123456789012:topic-to-delete";

        // Act
        var request = SnsQuerySerializers.DeserializeDeleteTopicRequest(Encoding.UTF8.GetBytes(queryString));

        // Assert
        request.TopicArn.ShouldBe("arn:aws:sns:us-east-1:123456789012:topic-to-delete");
    }

    [Test]
    public void SerializeGetTopicAttributesResponse_ShouldProduceValidXml()
    {
        // Arrange
        var response = new GetTopicAttributesResponse
        {
            Attributes = new Dictionary<string, string>
            {
                { "TopicArn", "arn:aws:sns:us-east-1:123456789012:my-topic" },
                { "DisplayName", "My Topic" },
                { "SubscriptionsConfirmed", "3" }
            }
        };
        var buffer = new ArrayBufferWriter<byte>();

        // Act
        SnsQuerySerializers.SerializeGetTopicAttributesResponse(response, buffer);

        // Assert
        var xml = Encoding.UTF8.GetString(buffer.WrittenSpan);
        var doc = XDocument.Parse(xml);

        var ns = XNamespace.Get("http://sns.amazonaws.com/doc/2010-03-31/");
        var root = doc.Root;
        root.ShouldNotBeNull();
        root!.Name.ShouldBe(ns + "GetTopicAttributesResponse");

        var result = root.Element(ns + "GetTopicAttributesResult");
        result.ShouldNotBeNull();

        var attributes = result!.Element(ns + "Attributes");
        attributes.ShouldNotBeNull();

        var entries = attributes!.Elements(ns + "entry").ToList();
        entries.Count.ShouldBe(3);

        // Verify attributes are present (order may vary)
        var attrDict = entries.ToDictionary(
            e => e.Element(ns + "key")!.Value,
            e => e.Element(ns + "value")!.Value
        );

        attrDict["TopicArn"].ShouldBe("arn:aws:sns:us-east-1:123456789012:my-topic");
        attrDict["DisplayName"].ShouldBe("My Topic");
        attrDict["SubscriptionsConfirmed"].ShouldBe("3");
    }

    [Test]
    public void DeserializeUnsubscribeRequest_ShouldParseCorrectly()
    {
        // Arrange
        var queryString = "SubscriptionArn=arn:aws:sns:us-east-1:123456789012:my-topic:12345678-1234-1234-1234-123456789012";

        // Act
        var request = SnsQuerySerializers.DeserializeUnsubscribeRequest(Encoding.UTF8.GetBytes(queryString));

        // Assert
        request.SubscriptionArn.ShouldBe("arn:aws:sns:us-east-1:123456789012:my-topic:12345678-1234-1234-1234-123456789012");
    }
}

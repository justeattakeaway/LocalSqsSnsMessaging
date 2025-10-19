using System.Text;
using System.Text.Json;
using Amazon.SQS.Model;
using LocalSqsSnsMessaging.Http.Handlers;
using Shouldly;

namespace LocalSqsSnsMessaging.Tests.Http;

/// <summary>
/// Acceptance tests for SqsJsonSerializers to verify request deserialization and response serialization
/// work correctly without requiring the full InMemoryAwsBus infrastructure.
/// </summary>
public sealed class SqsJsonSerializersTests
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = false };

    private static void AssertJsonEquals(string expected, string actual)
    {
        var expectedDoc = JsonDocument.Parse(expected);
        var actualDoc = JsonDocument.Parse(actual);
        var expectedJson = JsonSerializer.Serialize(expectedDoc, _jsonOptions);
        var actualJson = JsonSerializer.Serialize(actualDoc, _jsonOptions);
        actualJson.ShouldBe(expectedJson);
    }

    [Test]
    public void DeserializeSendMessageRequest_WithBasicMessage_ShouldParseCorrectly()
    {
        // Arrange
        var json = """
        {
            "queueUrl": "https://sqs.us-east-1.amazonaws.com/123456789012/test-queue",
            "messageBody": "Hello, World!",
            "delaySeconds": 5
        }
        """u8;
        using var stream = new MemoryStream(json.ToArray());

        // Act
        var request = SqsJsonSerializers.DeserializeSendMessageRequest(stream);

        // Assert
        request.QueueUrl.ShouldBe("https://sqs.us-east-1.amazonaws.com/123456789012/test-queue");
        request.MessageBody.ShouldBe("Hello, World!");
        request.DelaySeconds.ShouldBe(5);
    }

    [Test]
    public void DeserializeSendMessageRequest_WithMessageAttributes_ShouldParseCorrectly()
    {
        // Arrange
        var json = """
        {
            "queueUrl": "https://sqs.us-east-1.amazonaws.com/123456789012/test-queue",
            "messageBody": "Test message",
            "messageAttributes": {
                "CustomerId": {
                    "stringValue": "12345",
                    "dataType": "String"
                },
                "Priority": {
                    "stringValue": "1",
                    "dataType": "Number"
                }
            }
        }
        """u8;
        using var stream = new MemoryStream(json.ToArray());

        // Act
        var request = SqsJsonSerializers.DeserializeSendMessageRequest(stream);

        // Assert
        request.QueueUrl.ShouldBe("https://sqs.us-east-1.amazonaws.com/123456789012/test-queue");
        request.MessageBody.ShouldBe("Test message");
        request.MessageAttributes.ShouldNotBeNull();
        request.MessageAttributes.ShouldContainKey("CustomerId");
        request.MessageAttributes["CustomerId"].StringValue.ShouldBe("12345");
        request.MessageAttributes["CustomerId"].DataType.ShouldBe("String");
        request.MessageAttributes.ShouldContainKey("Priority");
        request.MessageAttributes["Priority"].StringValue.ShouldBe("1");
        request.MessageAttributes["Priority"].DataType.ShouldBe("Number");
    }

    [Test]
    public void SerializeSendMessageResponse_WithBasicResponse_ShouldProduceCorrectJson()
    {
        // Arrange
        var response = new SendMessageResponse
        {
            MessageId = "msg-12345",
            MD5OfMessageBody = "abc123def456",
            SequenceNumber = "seq-789"
        };
        using var stream = new MemoryStream();

        // Act
        SqsJsonSerializers.SerializeSendMessageResponse(response, stream);

        // Assert
        var json = Encoding.UTF8.GetString(stream.ToArray());
        var expected = """
        {
            "mD5OfMessageBody": "abc123def456",
            "messageId": "msg-12345",
            "sequenceNumber": "seq-789"
        }
        """;

        AssertJsonEquals(expected, json);
    }

    [Test]
    public void DeserializeReceiveMessageRequest_WithAllOptions_ShouldParseCorrectly()
    {
        // Arrange
        var json = """
        {
            "queueUrl": "https://sqs.us-east-1.amazonaws.com/123456789012/test-queue",
            "maxNumberOfMessages": 10,
            "visibilityTimeout": 30,
            "waitTimeSeconds": 20,
            "attributeNames": ["All"],
            "messageAttributeNames": [".*"]
        }
        """u8;
        using var stream = new MemoryStream(json.ToArray());

        // Act
        var request = SqsJsonSerializers.DeserializeReceiveMessageRequest(stream);

        // Assert
        request.QueueUrl.ShouldBe("https://sqs.us-east-1.amazonaws.com/123456789012/test-queue");
        request.MaxNumberOfMessages.ShouldBe(10);
        request.VisibilityTimeout.ShouldBe(30);
        request.WaitTimeSeconds.ShouldBe(20);
        #pragma warning disable CS0618 // Type or member is obsolete
        request.AttributeNames.ShouldContain("All");
        #pragma warning restore CS0618
        request.MessageAttributeNames.ShouldContain(".*");
    }

    [Test]
    public void SerializeReceiveMessageResponse_WithMessages_ShouldProduceCorrectJson()
    {
        // Arrange
        var response = new ReceiveMessageResponse
        {
            Messages =
            [
                new Message
                {
                    MessageId = "msg-1",
                    ReceiptHandle = "receipt-1",
                    MD5OfBody = "md5-1",
                    Body = "Message 1"
                },
                new Message
                {
                    MessageId = "msg-2",
                    ReceiptHandle = "receipt-2",
                    MD5OfBody = "md5-2",
                    Body = "Message 2"
                }
            ]
        };
        using var stream = new MemoryStream();

        // Act
        SqsJsonSerializers.SerializeReceiveMessageResponse(response, stream);

        // Assert
        var json = Encoding.UTF8.GetString(stream.ToArray());
        var expected = """
        {
            "messages": [
                {
                    "messageId": "msg-1",
                    "receiptHandle": "receipt-1",
                    "mD5OfBody": "md5-1",
                    "body": "Message 1"
                },
                {
                    "messageId": "msg-2",
                    "receiptHandle": "receipt-2",
                    "mD5OfBody": "md5-2",
                    "body": "Message 2"
                }
            ]
        }
        """;

        AssertJsonEquals(expected, json);
    }

    [Test]
    public void DeserializeCreateQueueRequest_WithAttributes_ShouldParseCorrectly()
    {
        // Arrange
        var json = """
        {
            "queueName": "my-test-queue.fifo",
            "attributes": {
                "FifoQueue": "true",
                "ContentBasedDeduplication": "true",
                "VisibilityTimeout": "30"
            }
        }
        """u8;
        using var stream = new MemoryStream(json.ToArray());

        // Act
        var request = SqsJsonSerializers.DeserializeCreateQueueRequest(stream);

        // Assert
        request.QueueName.ShouldBe("my-test-queue.fifo");
        request.Attributes.ShouldNotBeNull();
        request.Attributes.ShouldContainKey("FifoQueue");
        request.Attributes["FifoQueue"].ShouldBe("true");
        request.Attributes.ShouldContainKey("ContentBasedDeduplication");
        request.Attributes["ContentBasedDeduplication"].ShouldBe("true");
        request.Attributes.ShouldContainKey("VisibilityTimeout");
        request.Attributes["VisibilityTimeout"].ShouldBe("30");
    }

    [Test]
    public void SerializeCreateQueueResponse_ShouldProduceCorrectJson()
    {
        // Arrange
        var response = new CreateQueueResponse
        {
            QueueUrl = "https://sqs.us-east-1.amazonaws.com/123456789012/my-test-queue.fifo"
        };
        using var stream = new MemoryStream();

        // Act
        SqsJsonSerializers.SerializeCreateQueueResponse(response, stream);

        // Assert
        var json = Encoding.UTF8.GetString(stream.ToArray());
        var expected = """
        {
            "queueUrl": "https://sqs.us-east-1.amazonaws.com/123456789012/my-test-queue.fifo"
        }
        """;

        AssertJsonEquals(expected, json);
    }

    [Test]
    public void DeserializeDeleteMessageRequest_ShouldParseCorrectly()
    {
        // Arrange
        var json = """
        {
            "queueUrl": "https://sqs.us-east-1.amazonaws.com/123456789012/test-queue",
            "receiptHandle": "AQEBxyz123abc=="
        }
        """u8;
        using var stream = new MemoryStream(json.ToArray());

        // Act
        var request = SqsJsonSerializers.DeserializeDeleteMessageRequest(stream);

        // Assert
        request.QueueUrl.ShouldBe("https://sqs.us-east-1.amazonaws.com/123456789012/test-queue");
        request.ReceiptHandle.ShouldBe("AQEBxyz123abc==");
    }

    [Test]
    public void SerializeGetQueueAttributesResponse_ShouldProduceCorrectJson()
    {
        // Arrange
        var response = new GetQueueAttributesResponse
        {
            Attributes = new Dictionary<string, string>
            {
                { "QueueArn", "arn:aws:sqs:us-east-1:123456789012:test-queue" },
                { "ApproximateNumberOfMessages", "42" },
                { "VisibilityTimeout", "30" }
            }
        };
        using var stream = new MemoryStream();

        // Act
        SqsJsonSerializers.SerializeGetQueueAttributesResponse(response, stream);

        // Assert
        var json = Encoding.UTF8.GetString(stream.ToArray());
        var expected = """
        {
            "attributes": {
                "QueueArn": "arn:aws:sqs:us-east-1:123456789012:test-queue",
                "ApproximateNumberOfMessages": "42",
                "VisibilityTimeout": "30"
            }
        }
        """;

        AssertJsonEquals(expected, json);
    }
}

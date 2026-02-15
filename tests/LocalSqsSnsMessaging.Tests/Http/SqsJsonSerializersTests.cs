#pragma warning disable CS8602, CS8604
using System.Buffers;
using System.Text;
using System.Text.Json;
using LocalSqsSnsMessaging.Sqs.Model;
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
            "QueueUrl": "https://sqs.us-east-1.amazonaws.com/123456789012/test-queue",
            "MessageBody": "Hello, World!",
            "DelaySeconds": 5
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
            "QueueUrl": "https://sqs.us-east-1.amazonaws.com/123456789012/test-queue",
            "MessageBody": "Test message",
            "MessageAttributes": {
                "CustomerId": {
                    "StringValue": "12345",
                    "DataType": "String"
                },
                "Priority": {
                    "StringValue": "1",
                    "DataType": "Number"
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
        var buffer = new ArrayBufferWriter<byte>();

        // Act
        SqsJsonSerializers.SerializeSendMessageResponse(response, buffer);

        // Assert
        var json = Encoding.UTF8.GetString(buffer.WrittenSpan);
        var expected = """
        {
            "MD5OfMessageBody": "abc123def456",
            "MessageId": "msg-12345",
            "SequenceNumber": "seq-789"
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
            "QueueUrl": "https://sqs.us-east-1.amazonaws.com/123456789012/test-queue",
            "MaxNumberOfMessages": 10,
            "VisibilityTimeout": 30,
            "WaitTimeSeconds": 20,
            "AttributeNames": ["All"],
            "MessageAttributeNames": [".*"]
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
        request.AttributeNames.ShouldContain("All");
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
        var buffer = new ArrayBufferWriter<byte>();

        // Act
        SqsJsonSerializers.SerializeReceiveMessageResponse(response, buffer);

        // Assert
        var json = Encoding.UTF8.GetString(buffer.WrittenSpan);
        var expected = """
        {
            "Messages": [
                {
                    "MessageId": "msg-1",
                    "ReceiptHandle": "receipt-1",
                    "MD5OfBody": "md5-1",
                    "Body": "Message 1"
                },
                {
                    "MessageId": "msg-2",
                    "ReceiptHandle": "receipt-2",
                    "MD5OfBody": "md5-2",
                    "Body": "Message 2"
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
            "QueueName": "my-test-queue.fifo",
            "Attributes": {
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
        var buffer = new ArrayBufferWriter<byte>();

        // Act
        SqsJsonSerializers.SerializeCreateQueueResponse(response, buffer);

        // Assert
        var json = Encoding.UTF8.GetString(buffer.WrittenSpan);
        var expected = """
        {
            "QueueUrl": "https://sqs.us-east-1.amazonaws.com/123456789012/my-test-queue.fifo"
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
            "QueueUrl": "https://sqs.us-east-1.amazonaws.com/123456789012/test-queue",
            "ReceiptHandle": "AQEBxyz123abc=="
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
        var buffer = new ArrayBufferWriter<byte>();

        // Act
        SqsJsonSerializers.SerializeGetQueueAttributesResponse(response, buffer);

        // Assert
        var json = Encoding.UTF8.GetString(buffer.WrittenSpan);
        var expected = """
        {
            "Attributes": {
                "QueueArn": "arn:aws:sqs:us-east-1:123456789012:test-queue",
                "ApproximateNumberOfMessages": "42",
                "VisibilityTimeout": "30"
            }
        }
        """;

        AssertJsonEquals(expected, json);
    }

    [Test]
    public void DeserializeSendMessageRequest_WithMessageSystemAttributes_ShouldParseCorrectly()
    {
        // Arrange
        var json = """
        {
            "QueueUrl": "https://sqs.us-east-1.amazonaws.com/123456789012/test-queue",
            "MessageBody": "Test message",
            "MessageSystemAttributes": {
                "AWSTraceHeader": {
                    "StringValue": "Root=1-5759e988-bd862e3fe1be46a994272793",
                    "DataType": "String"
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
        request.MessageSystemAttributes.ShouldNotBeNull();
        request.MessageSystemAttributes.ShouldContainKey("AWSTraceHeader");
        request.MessageSystemAttributes["AWSTraceHeader"].StringValue.ShouldBe("Root=1-5759e988-bd862e3fe1be46a994272793");
        request.MessageSystemAttributes["AWSTraceHeader"].DataType.ShouldBe("String");

        // Ensure MessageAttributes is not populated
        (request.MessageAttributes == null || request.MessageAttributes.Count == 0).ShouldBeTrue();
    }

    [Test]
    public void DeserializeSendMessageRequest_WithBothMessageAttributesAndSystemAttributes_ShouldParseBothCorrectly()
    {
        // Arrange
        var json = """
        {
            "QueueUrl": "https://sqs.us-east-1.amazonaws.com/123456789012/test-queue",
            "MessageBody": "Test message",
            "MessageAttributes": {
                "CustomerId": {
                    "StringValue": "12345",
                    "DataType": "String"
                },
                "Priority": {
                    "StringValue": "1",
                    "DataType": "Number"
                }
            },
            "MessageSystemAttributes": {
                "AWSTraceHeader": {
                    "StringValue": "Root=1-5759e988-bd862e3fe1be46a994272793",
                    "DataType": "String"
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

        // Verify MessageAttributes
        request.MessageAttributes.ShouldNotBeNull();
        request.MessageAttributes.Count.ShouldBe(2);
        request.MessageAttributes.ShouldContainKey("CustomerId");
        request.MessageAttributes["CustomerId"].StringValue.ShouldBe("12345");
        request.MessageAttributes["CustomerId"].DataType.ShouldBe("String");
        request.MessageAttributes.ShouldContainKey("Priority");
        request.MessageAttributes["Priority"].StringValue.ShouldBe("1");
        request.MessageAttributes["Priority"].DataType.ShouldBe("Number");

        // Verify MessageSystemAttributes
        request.MessageSystemAttributes.ShouldNotBeNull();
        request.MessageSystemAttributes.Count.ShouldBe(1);
        request.MessageSystemAttributes.ShouldContainKey("AWSTraceHeader");
        request.MessageSystemAttributes["AWSTraceHeader"].StringValue.ShouldBe("Root=1-5759e988-bd862e3fe1be46a994272793");
        request.MessageSystemAttributes["AWSTraceHeader"].DataType.ShouldBe("String");

        // Ensure they don't pollute each other
        request.MessageAttributes.ShouldNotContainKey("AWSTraceHeader");
        request.MessageSystemAttributes.ShouldNotContainKey("CustomerId");
        request.MessageSystemAttributes.ShouldNotContainKey("Priority");
    }

    [Test]
    public void DeserializeSendMessageRequest_WithMessageSystemAttributesBinaryValue_ShouldParseCorrectly()
    {
        // Arrange
        var testData = "Hello, World!"u8.ToArray();
        var base64Data = Convert.ToBase64String(testData);
        var json = $$"""
        {
            "QueueUrl": "https://sqs.us-east-1.amazonaws.com/123456789012/test-queue",
            "MessageBody": "Test message",
            "MessageSystemAttributes": {
                "BinaryData": {
                    "BinaryValue": "{{base64Data}}",
                    "DataType": "Binary"
                }
            }
        }
        """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        // Act
        var request = SqsJsonSerializers.DeserializeSendMessageRequest(stream);

        // Assert
        request.MessageSystemAttributes.ShouldNotBeNull();
        request.MessageSystemAttributes.ShouldContainKey("BinaryData");
        request.MessageSystemAttributes["BinaryData"].BinaryValue.ShouldNotBeNull();
        request.MessageSystemAttributes["BinaryData"].BinaryValue.ToArray().ShouldBe(testData);
        request.MessageSystemAttributes["BinaryData"].DataType.ShouldBe("Binary");
    }

    [Test]
    public void DeserializeSendMessageRequest_WithMessageSystemAttributesStringListValues_ShouldParseCorrectly()
    {
        // Arrange
        var json = """
        {
            "QueueUrl": "https://sqs.us-east-1.amazonaws.com/123456789012/test-queue",
            "MessageBody": "Test message",
            "MessageSystemAttributes": {
                "Tags": {
                    "StringListValues": ["tag1", "tag2", "tag3"],
                    "DataType": "String.Array"
                }
            }
        }
        """u8;
        using var stream = new MemoryStream(json.ToArray());

        // Act
        var request = SqsJsonSerializers.DeserializeSendMessageRequest(stream);

        // Assert
        request.MessageSystemAttributes.ShouldNotBeNull();
        request.MessageSystemAttributes.ShouldContainKey("Tags");
        request.MessageSystemAttributes["Tags"].StringListValues.ShouldNotBeNull();
        request.MessageSystemAttributes["Tags"].StringListValues.Count.ShouldBe(3);
        request.MessageSystemAttributes["Tags"].StringListValues[0].ShouldBe("tag1");
        request.MessageSystemAttributes["Tags"].StringListValues[1].ShouldBe("tag2");
        request.MessageSystemAttributes["Tags"].StringListValues[2].ShouldBe("tag3");
        request.MessageSystemAttributes["Tags"].DataType.ShouldBe("String.Array");
    }

    [Test]
    public void DeserializeSendMessageRequest_WithAllMessageSystemAttributeTypes_ShouldParseCorrectly()
    {
        // Arrange
        var testBinaryData = "Binary data"u8.ToArray();
        var base64Data = Convert.ToBase64String(testBinaryData);
        var json = $$"""
        {
            "QueueUrl": "https://sqs.us-east-1.amazonaws.com/123456789012/test-queue",
            "MessageBody": "Test message",
            "MessageSystemAttributes": {
                "StringAttr": {
                    "StringValue": "test-string",
                    "DataType": "String"
                },
                "BinaryAttr": {
                    "BinaryValue": "{{base64Data}}",
                    "DataType": "Binary"
                },
                "StringListAttr": {
                    "StringListValues": ["item1", "item2"],
                    "DataType": "String.Array"
                }
            }
        }
        """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        // Act
        var request = SqsJsonSerializers.DeserializeSendMessageRequest(stream);

        // Assert
        request.MessageSystemAttributes.ShouldNotBeNull();
        request.MessageSystemAttributes.Count.ShouldBe(3);

        // String attribute
        request.MessageSystemAttributes["StringAttr"].StringValue.ShouldBe("test-string");
        request.MessageSystemAttributes["StringAttr"].DataType.ShouldBe("String");

        // Binary attribute
        request.MessageSystemAttributes["BinaryAttr"].BinaryValue.ToArray().ShouldBe(testBinaryData);
        request.MessageSystemAttributes["BinaryAttr"].DataType.ShouldBe("Binary");

        // String list attribute
        request.MessageSystemAttributes["StringListAttr"].StringListValues[0].ShouldBe("item1");
        request.MessageSystemAttributes["StringListAttr"].StringListValues[1].ShouldBe("item2");
        request.MessageSystemAttributes["StringListAttr"].DataType.ShouldBe("String.Array");
    }

    [Test]
    public void DeserializeSendMessageRequest_WithFifoAttributes_ShouldParseCorrectly()
    {
        // Arrange
        var json = """
        {
            "QueueUrl": "https://sqs.us-east-1.amazonaws.com/123456789012/test-queue.fifo",
            "MessageBody": "Test FIFO message",
            "MessageDeduplicationId": "dedup-123",
            "MessageGroupId": "group-456"
        }
        """u8;
        using var stream = new MemoryStream(json.ToArray());

        // Act
        var request = SqsJsonSerializers.DeserializeSendMessageRequest(stream);

        // Assert
        request.QueueUrl.ShouldBe("https://sqs.us-east-1.amazonaws.com/123456789012/test-queue.fifo");
        request.MessageBody.ShouldBe("Test FIFO message");
        request.MessageDeduplicationId.ShouldBe("dedup-123");
        request.MessageGroupId.ShouldBe("group-456");
    }

    [Test]
    public void DeserializeSendMessageRequest_WithCompleteRequest_ShouldParseAllFieldsCorrectly()
    {
        // Arrange - A complete request with all possible fields
        var json = """
        {
            "QueueUrl": "https://sqs.us-east-1.amazonaws.com/123456789012/test-queue.fifo",
            "MessageBody": "Complete test message",
            "DelaySeconds": 10,
            "MessageAttributes": {
                "UserAttribute": {
                    "StringValue": "user-value",
                    "DataType": "String"
                }
            },
            "MessageSystemAttributes": {
                "AWSTraceHeader": {
                    "StringValue": "Root=1-5759e988-bd862e3fe1be46a994272793",
                    "DataType": "String"
                }
            },
            "MessageDeduplicationId": "dedup-789",
            "MessageGroupId": "group-012"
        }
        """u8;
        using var stream = new MemoryStream(json.ToArray());

        // Act
        var request = SqsJsonSerializers.DeserializeSendMessageRequest(stream);

        // Assert
        request.QueueUrl.ShouldBe("https://sqs.us-east-1.amazonaws.com/123456789012/test-queue.fifo");
        request.MessageBody.ShouldBe("Complete test message");
        request.DelaySeconds.ShouldBe(10);

        request.MessageAttributes.ShouldNotBeNull();
        request.MessageAttributes.Count.ShouldBe(1);
        request.MessageAttributes["UserAttribute"].StringValue.ShouldBe("user-value");

        request.MessageSystemAttributes.ShouldNotBeNull();
        request.MessageSystemAttributes.Count.ShouldBe(1);
        request.MessageSystemAttributes["AWSTraceHeader"].StringValue.ShouldBe("Root=1-5759e988-bd862e3fe1be46a994272793");

        request.MessageDeduplicationId.ShouldBe("dedup-789");
        request.MessageGroupId.ShouldBe("group-012");
    }
}

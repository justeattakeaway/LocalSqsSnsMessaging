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

    [Test]
    public void DeserializeSendMessageRequest_WithMessageSystemAttributes_ShouldParseCorrectly()
    {
        // Arrange
        var json = """
        {
            "queueUrl": "https://sqs.us-east-1.amazonaws.com/123456789012/test-queue",
            "messageBody": "Test message",
            "messageSystemAttributes": {
                "AWSTraceHeader": {
                    "stringValue": "Root=1-5759e988-bd862e3fe1be46a994272793",
                    "dataType": "String"
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
            },
            "messageSystemAttributes": {
                "AWSTraceHeader": {
                    "stringValue": "Root=1-5759e988-bd862e3fe1be46a994272793",
                    "dataType": "String"
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
            "queueUrl": "https://sqs.us-east-1.amazonaws.com/123456789012/test-queue",
            "messageBody": "Test message",
            "messageSystemAttributes": {
                "BinaryData": {
                    "binaryValue": "{{base64Data}}",
                    "dataType": "Binary"
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
            "queueUrl": "https://sqs.us-east-1.amazonaws.com/123456789012/test-queue",
            "messageBody": "Test message",
            "messageSystemAttributes": {
                "Tags": {
                    "stringListValues": ["tag1", "tag2", "tag3"],
                    "dataType": "String.Array"
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
            "queueUrl": "https://sqs.us-east-1.amazonaws.com/123456789012/test-queue",
            "messageBody": "Test message",
            "messageSystemAttributes": {
                "StringAttr": {
                    "stringValue": "test-string",
                    "dataType": "String"
                },
                "BinaryAttr": {
                    "binaryValue": "{{base64Data}}",
                    "dataType": "Binary"
                },
                "StringListAttr": {
                    "stringListValues": ["item1", "item2"],
                    "dataType": "String.Array"
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
            "queueUrl": "https://sqs.us-east-1.amazonaws.com/123456789012/test-queue.fifo",
            "messageBody": "Test FIFO message",
            "messageDeduplicationId": "dedup-123",
            "messageGroupId": "group-456"
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
            "queueUrl": "https://sqs.us-east-1.amazonaws.com/123456789012/test-queue.fifo",
            "messageBody": "Complete test message",
            "delaySeconds": 10,
            "messageAttributes": {
                "UserAttribute": {
                    "stringValue": "user-value",
                    "dataType": "String"
                }
            },
            "messageSystemAttributes": {
                "AWSTraceHeader": {
                    "stringValue": "Root=1-5759e988-bd862e3fe1be46a994272793",
                    "dataType": "String"
                }
            },
            "messageDeduplicationId": "dedup-789",
            "messageGroupId": "group-012"
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

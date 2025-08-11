using System.Security.Cryptography;
using System.Text.Json;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Shouldly;
using MessageAttributeValue = Amazon.SimpleNotificationService.Model.MessageAttributeValue;

namespace LocalSqsSnsMessaging.Tests;

public abstract class SnsPublishAsyncTests
{
    private static TimeSpan DefaultShortWaitTime =>
        TimeSpan.FromMilliseconds(
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" ? 250 : 100
        );

    protected IAmazonSimpleNotificationService Sns = null!;
    protected IAmazonSQS Sqs = null!;
    protected string AccountId = null!;

    // LocalStack throws a different exception when validating on publish.
    // This method allows us to deviate from this behaviour until support is added to our implementation.
    protected abstract bool SupportsAttributeSizeValidation();

    [Test, Category("TimeBasedTests")]
    public async Task PublishAsync_WithRawDelivery_ShouldDeliverMessageDirectly(CancellationToken cancellationToken)
    {
        // Arrange
        var topicArn = $"arn:aws:sns:us-east-1:{AccountId}:MyTopic";
        var queueArn = $"arn:aws:sqs:us-east-1:{AccountId}:MyQueue";

        await SetupTopicAndQueue(topicArn, queueArn, isRawDelivery: true);

        var request = new PublishRequest
        {
            TopicArn = topicArn,
            Message = "Test message",
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                ["TestAttribute"] = new() { DataType = "String", StringValue = "TestValue" }
            }
        };

        // Act
        var response = await Sns.PublishAsync(request, cancellationToken);

        // Assert
        response.MessageId.ShouldNotBeNullOrEmpty();

        var queueUrlResponse = await Sqs.GetQueueUrlAsync(new GetQueueUrlRequest { QueueName = "MyQueue" },
            cancellationToken);
        var queueUrl = queueUrlResponse.QueueUrl;

        await WaitAsync(DefaultShortWaitTime);

        var sqsMessages = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MessageAttributeNames = ["All"]
        }, cancellationToken);

        var sqsMessage = sqsMessages.Messages.ShouldHaveSingleItem();
        sqsMessage!.Body.ShouldBe("Test message");
        sqsMessage.MessageAttributes.ShouldContainKey("TestAttribute");
        sqsMessage.MessageAttributes["TestAttribute"].StringValue.ShouldBe("TestValue");
    }

    [Test, Category("TimeBasedTests")]
    public async Task PublishAsync_WithRawDelivery_ShouldCalculateMD5OfBody(CancellationToken cancellationToken)
    {
        // Arrange
        var topicArn = $"arn:aws:sns:us-east-1:{AccountId}:MyTopic";
        var queueArn = $"arn:aws:sqs:us-east-1:{AccountId}:MyQueue";

        await SetupTopicAndQueue(topicArn, queueArn, isRawDelivery: true);

        var request = new PublishRequest
        {
            TopicArn = topicArn,
            Message = "Test message"
        };

#pragma warning disable CA1308
#pragma warning disable CA5351
        var expectedHash = Convert.ToHexString(MD5.HashData("Test message"u8.ToArray())).ToLowerInvariant();
#pragma warning restore CA5351
#pragma warning restore CA1308

        // Act
        var response = await Sns.PublishAsync(request, cancellationToken);

        // Assert
        response.MessageId.ShouldNotBeNullOrEmpty();

        var queueUrlResponse = await Sqs.GetQueueUrlAsync(new GetQueueUrlRequest { QueueName = "MyQueue" },
            cancellationToken);
        var queueUrl = queueUrlResponse.QueueUrl;

        await WaitAsync(DefaultShortWaitTime);

        var sqsMessages = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MessageAttributeNames = ["All"]
        }, cancellationToken);

        var sqsMessage = sqsMessages.Messages.ShouldHaveSingleItem();
        sqsMessage!.MD5OfBody.ShouldBe(expectedHash);
    }

    [Test, Category("TimeBasedTests")]
    public async Task PublishAsync_WithNonRawDelivery_ShouldWrapMessageInSNSFormat(CancellationToken cancellationToken)
    {
        // Arrange
        var topicArn = $"arn:aws:sns:us-east-1:{AccountId}:MyTopic";
        var queueArn = $"arn:aws:sqs:us-east-1:{AccountId}:MyQueue";

        await SetupTopicAndQueue(topicArn, queueArn, isRawDelivery: false);

        var request = new PublishRequest
        {
            TopicArn = topicArn,
            Subject = "Test Subject",
            Message = "Test message",
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                ["TestAttribute"] = new() { DataType = "String", StringValue = "TestValue" }
            }
        };

        // Act
        var response = await Sns.PublishAsync(request, cancellationToken);

        // Assert
        response.MessageId.ShouldNotBeNullOrEmpty();

        var queueUrlResponse = await Sqs.GetQueueUrlAsync(new GetQueueUrlRequest { QueueName = "MyQueue" },
            cancellationToken);
        var queueUrl = queueUrlResponse.QueueUrl;

        await WaitAsync(DefaultShortWaitTime);

        var sqsMessages = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MessageAttributeNames = ["All"]
        }, cancellationToken);

        var sqsMessage = sqsMessages.Messages.ShouldHaveSingleItem();

        // Parse the JSON body using JsonDocument
        using var jsonDocument = JsonDocument.Parse(sqsMessage!.Body);
        var root = jsonDocument.RootElement;

        root.GetProperty("Type").GetString().ShouldBe("Notification");
        root.GetProperty("MessageId").GetString().ShouldBe(response.MessageId);
        root.GetProperty("TopicArn").GetString().ShouldBe(topicArn);
        root.GetProperty("Subject").GetString().ShouldBe("Test Subject");
        root.GetProperty("Message").GetString().ShouldBe("Test message");
        root.GetProperty("Timestamp").GetString().ShouldNotBeNullOrEmpty();

        // Check MessageAttributes in the SNS message body
        var messageAttributes = root.GetProperty("MessageAttributes");
        messageAttributes.GetProperty("TestAttribute").GetProperty("Type").GetString().ShouldBe("String");
        messageAttributes.GetProperty("TestAttribute").GetProperty("Value").GetString().ShouldBe("TestValue");
    }

    [Test]
    public async Task PublishAsync_WithNonExistentTopic_ShouldThrowException(CancellationToken cancellationToken)
    {
        // Arrange
        var nonExistentTopicArn = $"arn:aws:sns:us-east-1:{AccountId}:NonExistentTopic";
        var request = new PublishRequest
        {
            TopicArn = nonExistentTopicArn,
            Message = "Test message"
        };

        // Act & Assert
        await Assert.ThrowsAsync<NotFoundException>(() =>
            Sns.PublishAsync(request, cancellationToken));
    }

    private async Task SetupTopicAndQueue(string topicArn, string queueArn, bool isRawDelivery)
    {
        // Setup topic
        await Sns.CreateTopicAsync(new CreateTopicRequest { Name = topicArn.Split(':').Last() });

        // Setup queue
        await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = queueArn.Split(':').Last() });

        // Setup subscription
        await Sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "sqs",
            Endpoint = queueArn,
            Attributes = new Dictionary<string, string>
            {
                ["RawMessageDelivery"] = isRawDelivery.ToString()
            }
        });
    }

    // Topic Attributes
    [Test]
    public async Task SetTopicAttributes_ShouldSetAndRetrieveAttributes(CancellationToken cancellationToken)
    {
        // Arrange
        var topicName = "TestTopic";
        var topicArn = $"arn:aws:sns:us-east-1:{AccountId}:{topicName}";

        // Create the topic
        var createTopicResponse = await Sns.CreateTopicAsync(new CreateTopicRequest { Name = topicName },
            cancellationToken);
        createTopicResponse.TopicArn.ShouldBe(topicArn);

        // Act - Set topic attributes
        await Sns.SetTopicAttributesAsync(new SetTopicAttributesRequest
        {
            TopicArn = topicArn,
            AttributeName = "DisplayName",
            AttributeValue = "Test Display Name"
        }, cancellationToken);

        await Sns.SetTopicAttributesAsync(new SetTopicAttributesRequest
        {
            TopicArn = topicArn,
            AttributeName = "DeliveryPolicy",
            AttributeValue = JsonSerializer.Serialize(new
            {
                http = new
                {
                    defaultHealthyRetryPolicy = new
                    {
                        minDelayTarget = 20,
                        maxDelayTarget = 20,
                        numRetries = 3,
                        numMaxDelayRetries = 0,
                        numNoDelayRetries = 0,
                        numMinDelayRetries = 0,
                        backoffFunction = "linear"
                    },
                    disableSubscriptionOverrides = false
                }
            })
        }, cancellationToken);

        // Assert - Retrieve and check topic attributes
        var getTopicAttributesResponse = await Sns.GetTopicAttributesAsync(new GetTopicAttributesRequest
        {
            TopicArn = topicArn
        }, cancellationToken);

        getTopicAttributesResponse.Attributes.ShouldContainKey("DisplayName");
        getTopicAttributesResponse.Attributes["DisplayName"].ShouldBe("Test Display Name");

        getTopicAttributesResponse.Attributes.ShouldContainKey("DeliveryPolicy");
        var deliveryPolicy =
            JsonSerializer.Deserialize<JsonElement>(getTopicAttributesResponse.Attributes["DeliveryPolicy"]);
        deliveryPolicy.GetProperty("http").GetProperty("defaultHealthyRetryPolicy").GetProperty("minDelayTarget")
            .GetInt32().ShouldBe(20);
        deliveryPolicy.GetProperty("http").GetProperty("defaultHealthyRetryPolicy").GetProperty("maxDelayTarget")
            .GetInt32().ShouldBe(20);
        deliveryPolicy.GetProperty("http").GetProperty("defaultHealthyRetryPolicy").GetProperty("numRetries").GetInt32()
            .ShouldBe(3);
    }

    [Test]
    public async Task SetTopicAttributes_ForNonExistentTopic_ShouldThrowException(CancellationToken cancellationToken)
    {
        // Arrange
        var nonExistentTopicArn = $"arn:aws:sns:us-east-1:{AccountId}:NonExistentTopic";

        // Act & Assert
        await Assert.ThrowsAsync<NotFoundException>(() =>
            Sns.SetTopicAttributesAsync(new SetTopicAttributesRequest
            {
                TopicArn = nonExistentTopicArn,
                AttributeName = "DisplayName",
                AttributeValue = "Some Name"
            }, cancellationToken));
    }

    // Subscriptions
    [Test]
    public async Task GetSubscriptionAttributes_ShouldRetrieveCorrectAttributes(CancellationToken cancellationToken)
    {
        // Arrange
        var topicName = "TestTopic";
        var queueName = "TestQueue";
        var topicArn = $"arn:aws:sns:us-east-1:{AccountId}:{topicName}";
        var queueArn = $"arn:aws:sqs:us-east-1:{AccountId}:{queueName}";

        // Create topic and queue
        await Sns.CreateTopicAsync(new CreateTopicRequest { Name = topicName }, cancellationToken);
        await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = queueName }, cancellationToken);

        // Subscribe queue to topic
        var subscribeResponse = await Sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "sqs",
            Endpoint = queueArn,
            Attributes = new Dictionary<string, string>
            {
                ["RawMessageDelivery"] = "true",
                ["FilterPolicy"] = JsonSerializer.Serialize(new { attribute = (string[]) ["value1", "value2"] })
            }
        }, cancellationToken);

        // Act
        var getAttributesResponse = await Sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest
        {
            SubscriptionArn = subscribeResponse.SubscriptionArn
        }, cancellationToken);

        // Assert
        getAttributesResponse.Attributes.ShouldContainKeyAndValue("SubscriptionArn", subscribeResponse.SubscriptionArn);
        getAttributesResponse.Attributes.ShouldContainKeyAndValue("TopicArn", topicArn);
        getAttributesResponse.Attributes.ShouldContainKeyAndValue("Protocol", "sqs");
        getAttributesResponse.Attributes.ShouldContainKeyAndValue("Endpoint", queueArn);
        getAttributesResponse.Attributes.ShouldContainKey("RawMessageDelivery");
        getAttributesResponse.Attributes["RawMessageDelivery"].ShouldBe("true", StringCompareShould.IgnoreCase);
        getAttributesResponse.Attributes.ShouldContainKey("FilterPolicy");
        getAttributesResponse.Attributes["FilterPolicy"].ShouldNotBeNullOrEmpty();

        var filterPolicy = JsonSerializer.Deserialize<JsonElement>(getAttributesResponse.Attributes["FilterPolicy"]);
        filterPolicy.GetProperty("attribute").EnumerateArray().Select(x => x.GetString()).ShouldBe(["value1", "value2"]);
    }

    [Test]
    public async Task GetSubscriptionAttributes_ForNonExistentSubscription_ShouldThrowException(CancellationToken cancellationToken)
    {
        // Arrange
        var nonExistentSubscriptionArn =
            $"arn:aws:sns:us-east-1:{AccountId}:TestTopic:12345678-1234-1234-1234-123456789012";

        // Act & Assert
        await Assert.ThrowsAsync<NotFoundException>(() =>
            Sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest
            {
                SubscriptionArn = nonExistentSubscriptionArn
            }, cancellationToken));
    }

    [Test]
    public async Task SetSubscriptionAttributes_ShouldUpdateAttributes(CancellationToken cancellationToken)
    {
        // Arrange
        var topicName = "TestTopic";
        var queueName = "TestQueue";
        var topicArn = $"arn:aws:sns:us-east-1:{AccountId}:{topicName}";
        var queueArn = $"arn:aws:sqs:us-east-1:{AccountId}:{queueName}";

        await Sns.CreateTopicAsync(new CreateTopicRequest { Name = topicName }, cancellationToken);
        await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = queueName }, cancellationToken);

        var subscribeResponse = await Sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "sqs",
            Endpoint = queueArn
        }, cancellationToken);

        // Act
        await Sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
        {
            SubscriptionArn = subscribeResponse.SubscriptionArn,
            AttributeName = "RawMessageDelivery",
            AttributeValue = "true"
        }, cancellationToken);

        var newFilterPolicy = JsonSerializer.Serialize(new { attribute = (string[]) ["newValue"] });
        await Sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
        {
            SubscriptionArn = subscribeResponse.SubscriptionArn,
            AttributeName = "FilterPolicy",
            AttributeValue = newFilterPolicy
        }, cancellationToken);

        // Assert
        var getAttributesResponse = await Sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest
        {
            SubscriptionArn = subscribeResponse.SubscriptionArn
        }, cancellationToken);

        getAttributesResponse.Attributes.ShouldContainKey("RawMessageDelivery");
        getAttributesResponse.Attributes["RawMessageDelivery"].ShouldBe("true", StringCompareShould.IgnoreCase);
        getAttributesResponse.Attributes.ShouldContainKey("FilterPolicy");
        getAttributesResponse.Attributes["FilterPolicy"].ShouldBeEquivalentTo(newFilterPolicy);
    }

    // List Subscriptions
    [Test]
    public async Task ListSubscriptionsAsync_ShouldReturnAllSubscriptions(CancellationToken cancellationToken)
    {
        // Arrange
        var topic1Name = "TestTopic1";
        var topic2Name = "TestTopic2";
        var queueName = "TestQueue";
        var topic1Arn = $"arn:aws:sns:us-east-1:{AccountId}:{topic1Name}";
        var topic2Arn = $"arn:aws:sns:us-east-1:{AccountId}:{topic2Name}";
        var queueArn = $"arn:aws:sqs:us-east-1:{AccountId}:{queueName}";

        await Sns.CreateTopicAsync(new CreateTopicRequest { Name = topic1Name }, cancellationToken);
        await Sns.CreateTopicAsync(new CreateTopicRequest { Name = topic2Name }, cancellationToken);
        await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = queueName }, cancellationToken);

        var sub1 = await Sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topic1Arn,
            Protocol = "sqs",
            Endpoint = queueArn
        }, cancellationToken);

        var sub2 = await Sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topic2Arn,
            Protocol = "sqs",
            Endpoint = queueArn
        }, cancellationToken);

        // Act
        var listResponse = await Sns.ListSubscriptionsAsync(cancellationToken);

        // Assert
        listResponse.Subscriptions.Count.ShouldBeGreaterThanOrEqualTo(2);
        listResponse.Subscriptions.ShouldContain(s => s.SubscriptionArn == sub1.SubscriptionArn);
        listResponse.Subscriptions.ShouldContain(s => s.SubscriptionArn == sub2.SubscriptionArn);
        listResponse.Subscriptions.ShouldContain(s => s.TopicArn == topic1Arn);
        listResponse.Subscriptions.ShouldContain(s => s.TopicArn == topic2Arn);
        listResponse.Subscriptions.ShouldAllBe(s => s.Protocol == "sqs");
        listResponse.Subscriptions.ShouldAllBe(s => s.Endpoint == queueArn);
    }

    [Test]
    public async Task ListSubscriptionsByTopicAsync_ShouldReturnSubscriptionsForSpecificTopic(CancellationToken cancellationToken)
    {
        // Arrange
        var topic1Name = "TestTopic1";
        var topic2Name = "TestTopic2";
        var queueName = "TestQueue";
        var topic1Arn = $"arn:aws:sns:us-east-1:{AccountId}:{topic1Name}";
        var topic2Arn = $"arn:aws:sns:us-east-1:{AccountId}:{topic2Name}";
        var queueArn = $"arn:aws:sqs:us-east-1:{AccountId}:{queueName}";

        await Sns.CreateTopicAsync(new CreateTopicRequest { Name = topic1Name }, cancellationToken);
        await Sns.CreateTopicAsync(new CreateTopicRequest { Name = topic2Name }, cancellationToken);
        await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = queueName }, cancellationToken);

        var sub1 = await Sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topic1Arn,
            Protocol = "sqs",
            Endpoint = queueArn
        }, cancellationToken);

        await Sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topic2Arn,
            Protocol = "sqs",
            Endpoint = queueArn
        }, cancellationToken);

        // Act
        var listResponse = await Sns.ListSubscriptionsByTopicAsync(new ListSubscriptionsByTopicRequest
        {
            TopicArn = topic1Arn
        }, cancellationToken);

        // Assert
        listResponse.Subscriptions.Count.ShouldBe(1);
        listResponse.Subscriptions.ShouldContain(s => s.SubscriptionArn == sub1.SubscriptionArn);
        listResponse.Subscriptions.ShouldAllBe(s => s.TopicArn == topic1Arn);
        listResponse.Subscriptions.ShouldAllBe(s => s.Protocol == "sqs");
        listResponse.Subscriptions.ShouldAllBe(s => s.Endpoint == queueArn);
    }

    [Test]
    public async Task ListSubscriptionsAsync_WithMoreThan100Subscriptions_ShouldReturnPaginatedResults(CancellationToken cancellationToken)
    {
        // Arrange
        var topicName = "TestTopic";
        var queueNamePrefix = "TestQueue";
        var topicArn = $"arn:aws:sns:us-east-1:{AccountId}:{topicName}";

        await Sns.CreateTopicAsync(new CreateTopicRequest { Name = topicName }, cancellationToken);

        // Create 150 subscriptions to ensure pagination
        for (int i = 0; i < 150; i++)
        {
            var queueName = $"{queueNamePrefix}{i}";
            var queueArn = $"arn:aws:sqs:us-east-1:{AccountId}:{queueName}";
            await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = queueName }, cancellationToken);
            await Sns.SubscribeAsync(new SubscribeRequest
            {
                TopicArn = topicArn,
                Protocol = "sqs",
                Endpoint = queueArn
            }, cancellationToken);
        }

        // Act
        var allSubscriptions =
            await Sns.Paginators
                .ListSubscriptions(new ListSubscriptionsRequest())
                .Subscriptions
                .ToListAsync(cancellationToken: cancellationToken);

        // Assert
        allSubscriptions.Count.ShouldBe(150, "because we created 150 subscriptions");
        allSubscriptions.Select(s => s.SubscriptionArn).ShouldBeUnique();
        allSubscriptions.Count(s => s.TopicArn == topicArn).ShouldBe(150);
        allSubscriptions.ShouldAllBe(s => s.Protocol == "sqs");
        allSubscriptions.ShouldAllBe(s => s.Endpoint.StartsWith($"arn:aws:sqs:us-east-1:{AccountId}:{queueNamePrefix}"));
    }

    protected abstract Task WaitAsync(TimeSpan delay);

    // FIFO scenarios
    [Test, Category("TimeBasedTests")]
    public async Task PublishAsync_ToFifoTopic_ShouldDeliverMessageToFifoQueue_InOrder(CancellationToken cancellationToken)
    {
        // Arrange
        var topicName = "MyFifoTopic.fifo";
        var queueName = "MyFifoQueue.fifo";
        var topicArn = $"arn:aws:sns:us-east-1:{AccountId}:{topicName}";
        var queueArn = $"arn:aws:sqs:us-east-1:{AccountId}:{queueName}";

        await SetupFifoTopicAndQueue(topicArn, queueArn, isRawDelivery: true);

        var messageGroupId = "TestGroup";
        PublishRequest[] messages =
        [
            new PublishRequest
            {
                TopicArn = topicArn,
                Message = "First message",
                MessageGroupId = messageGroupId,
                MessageDeduplicationId = "Dedup1"
            },
            new PublishRequest
            {
                TopicArn = topicArn,
                Message = "Second message",
                MessageGroupId = messageGroupId,
                MessageDeduplicationId = "Dedup2"
            },
            new PublishRequest
            {
                TopicArn = topicArn,
                Message = "Third message",
                MessageGroupId = messageGroupId,
                MessageDeduplicationId = "Dedup3"
            }
        ];

        // Act
        foreach (var message in messages)
        {
            await Sns.PublishAsync(message, cancellationToken);

            // Add a small delay between publishes to ensure distinct SendTimestamp
            await WaitAsync(DefaultShortWaitTime);
        }

        // Assert
        var queueUrlResponse = await Sqs.GetQueueUrlAsync(new GetQueueUrlRequest { QueueName = queueName }, cancellationToken);
        var queueUrl = queueUrlResponse.QueueUrl;

        await WaitAsync(DefaultShortWaitTime);

        var receivedMessages = (await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 10,
            MessageSystemAttributeNames = ["All"]
        }, cancellationToken)).Messages;

        receivedMessages.Count.ShouldBe(3, "we published 3 messages");

        // Detailed logging of received messages
        Console.WriteLine("Received messages in order:");
        foreach (var msg in receivedMessages)
        {
            Console.WriteLine($"Body: {msg.Body}");
            Console.WriteLine($"MessageId: {msg.MessageId}");
            Console.WriteLine($"SequenceNumber: {msg.Attributes?["SequenceNumber"]}");
            Console.WriteLine($"MessageDeduplicationId: {msg.Attributes?["MessageDeduplicationId"]}");
            Console.WriteLine($"MessageGroupId: {msg.Attributes?["MessageGroupId"]}");
            Console.WriteLine($"SentTimestamp: {msg.Attributes?["SentTimestamp"]}");
            Console.WriteLine("---");
        }

        // Check the order based on SequenceNumber
        var orderedMessages = receivedMessages
            .OrderBy(m => Int128.Parse(m.Attributes!["SequenceNumber"], NumberFormatInfo.InvariantInfo)).ToList();

        orderedMessages[0].Body.ShouldBe("First message", "it was published first");
        orderedMessages[1].Body.ShouldBe("Second message", "it was published second");
        orderedMessages[2].Body.ShouldBe("Third message", "it was published third");

        // Verify that MessageGroupId is consistent
        receivedMessages.ShouldAllBe(m => m.Attributes!["MessageGroupId"] == messageGroupId);

        // Verify that MessageDeduplicationId is unique for each message
        receivedMessages.Select(m => m.Attributes!["MessageDeduplicationId"]).Distinct().Count().ShouldBe(3);

        // Check if the order matches the publish order
        if (receivedMessages[1].Body != "Second message" ||
            !string.Equals(receivedMessages[2].Body, "Third message", StringComparison.Ordinal))
        {
            Console.WriteLine("Warning: Messages were not received in the exact order they were published.");
            Console.WriteLine("This might be due to how FIFO queues handle nearly simultaneous publishes.");
            Console.WriteLine("Consider the SequenceNumber and SentTimestamp for the correct ordering.");
        }
    }

    [Test, Category("TimeBasedTests")]
    public async Task PublishAsync_ToFifoTopic_ShouldPreventDuplicates(CancellationToken cancellationToken)
    {
        // Arrange
        var topicName = "DedupFifoTopic.fifo";
        var queueName = "DedupFifoQueue.fifo";
        var topicArn = $"arn:aws:sns:us-east-1:{AccountId}:{topicName}";
        var queueArn = $"arn:aws:sqs:us-east-1:{AccountId}:{queueName}";

        await SetupFifoTopicAndQueue(topicArn, queueArn, isRawDelivery: true);

        var messageGroupId = "TestGroup";
        var deduplicationId = "UniqueDedup";
        var message = new PublishRequest
        {
            TopicArn = topicArn,
            Message = "Duplicate message",
            MessageGroupId = messageGroupId,
            MessageDeduplicationId = deduplicationId
        };

        // Act
        await Sns.PublishAsync(message, cancellationToken);
        await Sns.PublishAsync(message, cancellationToken); // Attempt to send duplicate

        // Assert
        var queueUrlResponse = await Sqs.GetQueueUrlAsync(new GetQueueUrlRequest { QueueName = queueName }, cancellationToken);
        var queueUrl = queueUrlResponse.QueueUrl;

        await WaitAsync(DefaultShortWaitTime);

        var receivedMessages = (await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 10,
            MessageSystemAttributeNames = ["All"]
        }, cancellationToken)).Messages;

        receivedMessages.Count.ShouldBe(1, "because the second message should be deduplicated");
        receivedMessages[0].Body.ShouldBe("Duplicate message");
        receivedMessages[0].Attributes["MessageDeduplicationId"].ShouldBe(deduplicationId);
    }

    [Test, Category("TimeBasedTests")]
    public async Task PublishAsync_ToFifoTopic_WithMultipleMessageGroups_ShouldMaintainOrderWithinGroups(CancellationToken cancellationToken)
    {
        // Arrange
        var topicName = "MultiGroupFifoTopic.fifo";
        var queueName = "MultiGroupFifoQueue.fifo";
        var topicArn = $"arn:aws:sns:us-east-1:{AccountId}:{topicName}";
        var queueArn = $"arn:aws:sqs:us-east-1:{AccountId}:{queueName}";

        await SetupFifoTopicAndQueue(topicArn, queueArn, isRawDelivery: true);

        var messages = new[]
        {
            new PublishRequest
            {
                TopicArn = topicArn,
                Message = "Group A - First",
                MessageGroupId = "GroupA",
                MessageDeduplicationId = "DedupA1"
            },
            new PublishRequest
            {
                TopicArn = topicArn,
                Message = "Group B - First",
                MessageGroupId = "GroupB",
                MessageDeduplicationId = "DedupB1"
            },
            new PublishRequest
            {
                TopicArn = topicArn,
                Message = "Group A - Second",
                MessageGroupId = "GroupA",
                MessageDeduplicationId = "DedupA2"
            },
            new PublishRequest
            {
                TopicArn = topicArn,
                Message = "Group B - Second",
                MessageGroupId = "GroupB",
                MessageDeduplicationId = "DedupB2"
            }
        };

        // Act
        foreach (var message in messages)
        {
            await Sns.PublishAsync(message, cancellationToken);
            await WaitAsync(DefaultShortWaitTime);
        }

        // Assert
        var queueUrlResponse =
            await Sqs.GetQueueUrlAsync(new GetQueueUrlRequest { QueueName = queueName }, cancellationToken);
        var queueUrl = queueUrlResponse.QueueUrl;

        await WaitAsync(DefaultShortWaitTime);

        var receivedMessages = (await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 10,
            MessageSystemAttributeNames = ["All"]
        }, cancellationToken)).Messages;

        receivedMessages.Count.ShouldBe(4);

        var groupAMessages = receivedMessages.Where(m => m.Attributes["MessageGroupId"] == "GroupA").ToList();
        var groupBMessages = receivedMessages.Where(m => m.Attributes["MessageGroupId"] == "GroupB").ToList();

        groupAMessages.Select(m => m.Body).ShouldBe(["Group A - First", "Group A - Second"]);
        groupBMessages.Select(m => m.Body).ShouldBe(["Group B - First", "Group B - Second"]);
    }

    private async Task SetupFifoTopicAndQueue(string topicArn, string queueArn, bool isRawDelivery)
    {
        // Setup FIFO topic
        await Sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = topicArn.Split(':').Last(),
            Attributes = new Dictionary<string, string>
            {
                ["FifoTopic"] = "true",
                ["ContentBasedDeduplication"] = "false"
            }
        });

        // Setup FIFO queue
        await Sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = queueArn.Split(':').Last(),
            Attributes = new Dictionary<string, string>
            {
                ["FifoQueue"] = "true",
                ["ContentBasedDeduplication"] = "false"
            }
        });

        // Setup subscription
        await Sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "sqs",
            Endpoint = queueArn,
            Attributes = new Dictionary<string, string>
            {
#pragma warning disable CA1308
                ["RawMessageDelivery"] = isRawDelivery.ToString().ToLowerInvariant()
#pragma warning restore CA1308
            }
        });
    }

    [Test]
    public async Task PublishAsync_MessageExceedsMaximumSize_ThrowsInvalidParameterException(CancellationToken cancellationToken)
    {
        // Arrange
        var topicName = "TestTopic";
        var topicArn = $"arn:aws:sns:us-east-1:{AccountId}:{topicName}";
        await Sns.CreateTopicAsync(new CreateTopicRequest { Name = topicName }, cancellationToken);

        var request = new PublishRequest
        {
            TopicArn = topicArn,
            Message = new string('x', 262145) // Exceeds 256KB
        };

        // Act & Assert
        await Assert.ThrowsAsync<InvalidParameterException>(() =>
            Sns.PublishAsync(request, cancellationToken));
    }

    [Test]
    public async Task PublishAsync_MessageAttributesExceedMaximumSize_ThrowsInvalidParameterException(CancellationToken cancellationToken)
    {
        // Arrange
        var topicName = "TestTopic";
        var topicArn = $"arn:aws:sns:us-east-1:{AccountId}:{topicName}";
        await Sns.CreateTopicAsync(new CreateTopicRequest { Name = topicName }, cancellationToken);

        var request = new PublishRequest
        {
            TopicArn = topicArn,
            Message = new string('x', 200000),
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                [new string('a', 1000)] = new() // Long attribute name
                {
                    DataType = "String",
                    StringValue = new string('y', 61145) // Push total over 256KB
                }
            }
        };

        // Act & Assert
        var testAction = () =>
            Sns.PublishAsync(request, cancellationToken);

        if (SupportsAttributeSizeValidation())
        {
            await Assert.ThrowsAsync<InvalidParameterValueException>(testAction);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidParameterException>(testAction);
        }
    }

    [Test]
    public async Task PublishAsync_ExactlyMaximumSize_Succeeds(CancellationToken cancellationToken)
    {
        // Arrange
        var topicName = "TestTopic";
        var queueName = "TestQueue";
        var topicArn = $"arn:aws:sns:us-east-1:{AccountId}:{topicName}";
        var queueArn = $"arn:aws:sqs:us-east-1:{AccountId}:{queueName}";

        await SetupTopicAndQueue(topicArn, queueArn, isRawDelivery: true);

        var request = new PublishRequest
        {
            TopicArn = topicArn,
            Message = new string('x', 262144) // Exactly 256KB
        };

        // Act
        var response = await Sns.PublishAsync(request, cancellationToken);

        // Assert
        response.MessageId.ShouldNotBeNullOrEmpty();
    }

    [Test]
    public async Task PublishAsync_WithSubjectAndMessageAttributes_ExceedsLimit_ThrowsInvalidParameterException(CancellationToken cancellationToken)
    {
        // Arrange
        var topicName = "TestTopic";
        var topicArn = $"arn:aws:sns:us-east-1:{AccountId}:{topicName}";
        await Sns.CreateTopicAsync(new CreateTopicRequest { Name = topicName }, cancellationToken);

        var messageBody = new string('x', 200_000);

        var request = new PublishRequest
        {
            TopicArn = topicArn,
            Subject = new string('s', 50),
            Message = messageBody,
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                // Long attribute name (contributes to size)
                [new string('a', 1000)] = new MessageAttributeValue
                {
                    DataType = "String", // 6 bytes
                    StringValue = new string('y', 62000)
                },
                // Another attribute to push us over the limit
                [new string('b', 100)] = new MessageAttributeValue
                {
                    DataType = "Number", // 6 bytes
                    StringValue = "123"
                }
            }
        };

        // Act & Assert
        var testAction = () =>
            Sns.PublishAsync(request, cancellationToken);

        if (SupportsAttributeSizeValidation())
        {
            await Assert.ThrowsAsync<InvalidParameterValueException>(testAction);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidParameterException>(testAction);
        }
    }
}

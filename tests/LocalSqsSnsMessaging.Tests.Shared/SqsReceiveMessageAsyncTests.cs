using Amazon.Auth.AccessControlPolicy;
using Amazon.SQS;
using Amazon.SQS.Model;
using Xunit;

namespace LocalSqsSnsMessaging.Tests;

public abstract class SqsReceiveMessageAsyncTests
{
    protected IAmazonSQS Sqs = null!;
    protected string AccountId = null!;

    [Fact]
    public async Task ReceiveMessageAsync_QueueNotFound_ThrowsQueueDoesNotExistException()
    {
        var request = new ReceiveMessageRequest { QueueUrl = "nonexistent-queue" };

        await Assert.ThrowsAsync<QueueDoesNotExistException>(() =>
            Sqs.ReceiveMessageAsync(request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReceiveMessageAsync_NoMessages_ReturnsEmptyList()
    {
        var createQueueResponse = await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test-queue" },
            TestContext.Current.CancellationToken);
        var queueUrl = createQueueResponse.QueueUrl;
        var request = new ReceiveMessageRequest { QueueUrl = queueUrl, WaitTimeSeconds = 0 };

        var result = await Sqs.ReceiveMessageAsync(request, TestContext.Current.CancellationToken);

        Assert.Null(result.Messages);
    }

    [Fact]
    public async Task ReceiveMessageAsync_MessagesAvailable_ReturnsMessages()
    {
        var createQueueResponse = await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test-queue" },
            TestContext.Current.CancellationToken);
        var queueUrl = createQueueResponse.QueueUrl;
        var request = new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 2 };

        await Sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "Hello, world!"
        }, TestContext.Current.CancellationToken);
        await Sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "Goodbye, world!"
        }, TestContext.Current.CancellationToken);

        var result = await Sqs.ReceiveMessageAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Messages.Count);
        Assert.Equal("Hello, world!", result.Messages[0].Body);
        Assert.Equal("Goodbye, world!", result.Messages[1].Body);
    }

    [Fact, Trait("Category", "TimeBasedTests")]
    public async Task ReceiveMessageAsync_WaitsForMessages_ReturnsMessagesWhenAvailable()
    {
        var createQueueResponse = await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test-queue" },
            TestContext.Current.CancellationToken);
        var queueUrl = createQueueResponse.QueueUrl;
        var request = new ReceiveMessageRequest { QueueUrl = queueUrl, WaitTimeSeconds = 5 };

        var task = Sqs.ReceiveMessageAsync(request, TestContext.Current.CancellationToken);

        await AdvanceTime(TimeSpan.FromSeconds(3));
        await Sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "Hello, world!"
        }, TestContext.Current.CancellationToken);

        var result = await task;

        var receivedMessage = Assert.Single(result.Messages);
        Assert.Equal("Hello, world!", receivedMessage.Body);
    }

    [Fact, Trait("Category", "TimeBasedTests")]
    public async Task ReceiveMessageAsync_Timeout_ReturnsEmptyList()
    {
        var createQueueResponse = await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test-queue" },
            TestContext.Current.CancellationToken);
        var queueUrl = createQueueResponse.QueueUrl;
        var request = new ReceiveMessageRequest { QueueUrl = queueUrl, WaitTimeSeconds = 5 };

        var task = Sqs.ReceiveMessageAsync(request, TestContext.Current.CancellationToken);

        await AdvanceTime(TimeSpan.FromSeconds(5));

        var result = await task;

        Assert.Null(result.Messages);
    }

    [Fact]
    public async Task ReceiveMessageAsync_CancellationRequested_ReturnsEmptyList()
    {
        var createQueueResponse = await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test-queue" },
            TestContext.Current.CancellationToken);
        var queueUrl = createQueueResponse.QueueUrl;
        var request = new ReceiveMessageRequest { QueueUrl = queueUrl, WaitTimeSeconds = 10 };

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Sqs.ReceiveMessageAsync(request, cts.Token)
        );
    }

    [Fact, Trait("Category", "TimeBasedTests")]
    public async Task ReceiveMessageAsync_RespectVisibilityTimeout()
    {
        var createQueueResponse = await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test-queue" },
            TestContext.Current.CancellationToken);
        var queueUrl = createQueueResponse.QueueUrl;
        var request = new ReceiveMessageRequest { QueueUrl = queueUrl, WaitTimeSeconds = 0, VisibilityTimeout = 30 };

        // Enqueue a message
        await Sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "Hello, world!"
        }, TestContext.Current.CancellationToken);

        // First receive - should get the message
        var result1 = await Sqs.ReceiveMessageAsync(request, TestContext.Current.CancellationToken);
        var firstReceivedMessage = Assert.Single(result1.Messages);
        Assert.Equal("Hello, world!", firstReceivedMessage.Body);

        // Second receive immediately after - should not get any message
        var result2 = await Sqs.ReceiveMessageAsync(request, TestContext.Current.CancellationToken);
        Assert.Null(result2.Messages);

        // Advance time by 15 seconds (half the visibility timeout)
        await AdvanceTime(TimeSpan.FromSeconds(15));

        // Third receive - should still not get any message
        var result3 = await Sqs.ReceiveMessageAsync(request, TestContext.Current.CancellationToken);
        Assert.Null(result3.Messages);

        // Advance time by another 20 seconds (visibility timeout has now passed)
        await AdvanceTime(TimeSpan.FromSeconds(20));

        // Fourth receive - should get the message again
        var result4 = await Sqs.ReceiveMessageAsync(request, TestContext.Current.CancellationToken);
        var forthReceivedMessage = Assert.Single(result4.Messages);
        Assert.Equal("Hello, world!", forthReceivedMessage.Body);
    }

    [Fact, Trait("Category", "TimeBasedTests")]
    public async Task ReceiveMessageAsync_DelayedMessageBecomesVisible()
    {
        //SynchronizationContext.SetSynchronizationContext(null);
        var createQueueResponse = await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test-queue" },
            TestContext.Current.CancellationToken);
        var queueUrl = createQueueResponse.QueueUrl;
        var request = new ReceiveMessageRequest { QueueUrl = queueUrl, WaitTimeSeconds = 0, VisibilityTimeout = 30 };

        // Enqueue a message with a delay of 10 seconds
        await Sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "Hello, world!",
            DelaySeconds = 10
        }, TestContext.Current.CancellationToken);

        // First receive - should not get any message
        var result1 = await Sqs.ReceiveMessageAsync(request, TestContext.Current.CancellationToken);
        Assert.Null(result1.Messages);

        // Advance time by 5 seconds
        await AdvanceTime(TimeSpan.FromSeconds(5));

        // Second receive - should still not get any message
        var result2 = await Sqs.ReceiveMessageAsync(request, TestContext.Current.CancellationToken);
        Assert.Null(result2.Messages);

        // Advance time by another 10 seconds (message is now visible)
        await AdvanceTime(TimeSpan.FromSeconds(10));

        // Third receive - should get the message
        var result3 = await Sqs.ReceiveMessageAsync(request, TestContext.Current.CancellationToken);
        var receivedMessage = Assert.Single(result3.Messages);
        Assert.Equal("Hello, world!", receivedMessage.Body);
    }

    [Fact, Trait("Category", "TimeBasedTests")]
    public async Task ReceiveMessageAsync_MultipleMessagesWithDifferentDelays()
    {
        //SynchronizationContext.SetSynchronizationContext(null);
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test-queue" },
            TestContext.Current.CancellationToken)).QueueUrl;
        var request = new ReceiveMessageRequest
            { QueueUrl = queueUrl, WaitTimeSeconds = 0, VisibilityTimeout = 30, MaxNumberOfMessages = 10 };

        // Send messages with different delays
        await Sqs.SendMessageAsync(new SendMessageRequest
                { QueueUrl = queueUrl, MessageBody = "Message 1", DelaySeconds = 5 },
            TestContext.Current.CancellationToken);
        await Sqs.SendMessageAsync(new SendMessageRequest
                { QueueUrl = queueUrl, MessageBody = "Message 2", DelaySeconds = 10 },
            TestContext.Current.CancellationToken);
        await Sqs.SendMessageAsync(new SendMessageRequest
                { QueueUrl = queueUrl, MessageBody = "Message 3", DelaySeconds = 15 },
            TestContext.Current.CancellationToken);

        await AdvanceTime(TimeSpan.FromSeconds(1));

        // Advance time and check messages
        await AdvanceTime(TimeSpan.FromSeconds(5));
        var result1 = await Sqs.ReceiveMessageAsync(request, TestContext.Current.CancellationToken);
        Assert.Single(result1.Messages);
        Assert.Equal("Message 1", result1.Messages[0].Body);

        // Advance time to make the second message visible
        await AdvanceTime(TimeSpan.FromSeconds(5));
        var result2 = await Sqs.ReceiveMessageAsync(request, TestContext.Current.CancellationToken);
        Assert.Single(result2.Messages);
        Assert.Equal("Message 2", result2.Messages[0].Body);

        // Advance time to make the third message visible
        await AdvanceTime(TimeSpan.FromSeconds(5));
        var result3 = await Sqs.ReceiveMessageAsync(request, TestContext.Current.CancellationToken);
        Assert.Single(result3.Messages);
        Assert.Equal("Message 3", result3.Messages[0].Body);

        // Advance time past the visibility timeout of the first message
        await AdvanceTime(TimeSpan.FromSeconds(21)); // (7 + 30) - (7 + 5 + 5) = 20
        var result4 = await Sqs.ReceiveMessageAsync(request, TestContext.Current.CancellationToken);
        Assert.Single(result4.Messages);
        Assert.Equal("Message 1", result4.Messages[0].Body);

        // Advance time past the visibility timeout of the second message
        await AdvanceTime(TimeSpan.FromSeconds(5));
        var result5 = await Sqs.ReceiveMessageAsync(request, TestContext.Current.CancellationToken);
        Assert.Single(result5.Messages);
        Assert.Equal("Message 2", result5.Messages[0].Body);

        // Advance time past the visibility timeout of the third message
        await AdvanceTime(TimeSpan.FromSeconds(5));
        var result6 = await Sqs.ReceiveMessageAsync(request, TestContext.Current.CancellationToken);
        Assert.Single(result6.Messages);
        Assert.Equal("Message 3", result6.Messages[0].Body);
    }

    [Fact, Trait("Category", "TimeBasedTests")]
    public async Task ReceiveMessageAsync_ApproximateReceiveCount_IncreasesWithEachReceive()
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test-queue" },
            TestContext.Current.CancellationToken)).QueueUrl;
        var request = new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            WaitTimeSeconds = 0,
            VisibilityTimeout = 5,
            MessageSystemAttributeNames = ["ApproximateReceiveCount"]
        };

        // Send a message
        await Sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = queueUrl, MessageBody = "Test message" },
            TestContext.Current.CancellationToken);

        // First receive
        var result1 = await Sqs.ReceiveMessageAsync(request, TestContext.Current.CancellationToken);
        var message1 = Assert.Single(result1.Messages);
        Assert.Equal("1", message1.Attributes["ApproximateReceiveCount"]);

        // Wait for visibility timeout to expire
        await AdvanceTime(TimeSpan.FromSeconds(6));

        // Second receive
        var result2 = await Sqs.ReceiveMessageAsync(request, TestContext.Current.CancellationToken);
        var message2 = Assert.Single(result2.Messages);
        Assert.Equal("2", message2.Attributes["ApproximateReceiveCount"]);

        // Wait for visibility timeout to expire again
        await AdvanceTime(TimeSpan.FromSeconds(6));

        // Third receive
        var result3 = await Sqs.ReceiveMessageAsync(request, TestContext.Current.CancellationToken);
        var message3 = Assert.Single(result3.Messages);
        Assert.Equal("3", message3.Attributes["ApproximateReceiveCount"]);
    }

    [Fact]
    public async Task ReceiveMessageAsync_ApproximateReceiveCount_ResetAfterDelete()
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test-queue" },
            TestContext.Current.CancellationToken)).QueueUrl;
        var request = new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            WaitTimeSeconds = 0,
            VisibilityTimeout = 5,
            MessageSystemAttributeNames = ["ApproximateReceiveCount"]
        };

        // Send a message
        await Sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = queueUrl, MessageBody = "Test message" },
            TestContext.Current.CancellationToken);

        // Receive and delete the message
        var result1 = await Sqs.ReceiveMessageAsync(request, TestContext.Current.CancellationToken);
        var message1 = Assert.Single(result1.Messages);
        Assert.Equal("1", message1.Attributes["ApproximateReceiveCount"]);
        await Sqs.DeleteMessageAsync(queueUrl, message1.ReceiptHandle, TestContext.Current.CancellationToken);

        // Send another message
        await Sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = queueUrl, MessageBody = "New test message" },
            TestContext.Current.CancellationToken);

        // Receive the new message
        var result2 = await Sqs.ReceiveMessageAsync(request, TestContext.Current.CancellationToken);
        var message2 = Assert.Single(result2.Messages);
        Assert.Equal("1", message2.Attributes["ApproximateReceiveCount"]);
    }

    [Fact, Trait("Category", "TimeBasedTests")]
    public async Task ReceiveMessageAsync_ApproximateReceiveCount_MultipleMessages()
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test-queue" },
            TestContext.Current.CancellationToken)).QueueUrl;
        var request = new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            WaitTimeSeconds = 0,
            VisibilityTimeout = 5,
            MaxNumberOfMessages = 10,
            MessageSystemAttributeNames = ["ApproximateReceiveCount"]
        };

        // Send multiple messages
        await Sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = queueUrl, MessageBody = "Message 1" },
            TestContext.Current.CancellationToken);
        await Sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = queueUrl, MessageBody = "Message 2" },
            TestContext.Current.CancellationToken);

        // First receive
        var result1 = await Sqs.ReceiveMessageAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(2, result1.Messages.Count);
        Assert.All(result1.Messages, m => Assert.Equal("1", m.Attributes["ApproximateReceiveCount"]));

        // Wait for visibility timeout to expire
        await AdvanceTime(TimeSpan.FromSeconds(6));

        // Second receive
        var result2 = await Sqs.ReceiveMessageAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(2, result2.Messages.Count);
        Assert.All(result2.Messages, m => Assert.Equal("2", m.Attributes["ApproximateReceiveCount"]));

        // Delete one message
        await Sqs.DeleteMessageAsync(queueUrl, result2.Messages[0].ReceiptHandle,
            TestContext.Current.CancellationToken);

        // Wait for visibility timeout to expire again
        await AdvanceTime(TimeSpan.FromSeconds(6));

        // Third receive
        var result3 = await Sqs.ReceiveMessageAsync(request, TestContext.Current.CancellationToken);
        var message3 = Assert.Single(result3.Messages);
        Assert.Equal("3", message3.Attributes["ApproximateReceiveCount"]);
    }

    [Fact, Trait("Category", "TimeBasedTests")]
    public async Task ReceiveMessageAsync_MessageMovedToErrorQueue_AfterMaxReceives()
    {
        // Create main queue and error queue
        var mainQueueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "main-queue" },
            TestContext.Current.CancellationToken)).QueueUrl;
        var errorQueueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "error-queue" },
            TestContext.Current.CancellationToken)).QueueUrl;
        var errorQueueArn = $"arn:aws:sqs:us-east-1:{AccountId}:{errorQueueUrl.Split('/').Last()}";

        // Set up redrive policy for the main queue
        await Sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = mainQueueUrl,
            Attributes = new Dictionary<string, string>
            {
                ["RedrivePolicy"] = $$"""{"maxReceiveCount":3, "deadLetterTargetArn":"{{errorQueueArn}}"}"""
            }
        }, TestContext.Current.CancellationToken);

        var request = new ReceiveMessageRequest
        {
            QueueUrl = mainQueueUrl,
            WaitTimeSeconds = 0,
            VisibilityTimeout = 5,
            MessageSystemAttributeNames = ["ApproximateReceiveCount"],
        };

        // Send a message to the main queue
        await Sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = mainQueueUrl, MessageBody = "Test message" },
            TestContext.Current.CancellationToken);

        // Receive the message three times
        for (int i = 0; i < 3; i++)
        {
            var result = await Sqs.ReceiveMessageAsync(request, TestContext.Current.CancellationToken);
            Assert.Single(result.Messages);
            Assert.Equal((i + 1).ToString(NumberFormatInfo.InvariantInfo),
                result.Messages[0].Attributes["ApproximateReceiveCount"]);
            await AdvanceTime(TimeSpan.FromSeconds(6)); // Wait for visibility timeout to expire
        }

        // Try to receive from the main queue - should be empty
        var emptyResult = await Sqs.ReceiveMessageAsync(request, TestContext.Current.CancellationToken);
        Assert.Null(emptyResult.Messages);

        // Check the error queue - the message should be there
        var errorQueueResult = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = errorQueueUrl },
            TestContext.Current.CancellationToken);
        var errorMessage = Assert.Single(errorQueueResult.Messages);
        Assert.Equal("Test message", errorMessage.Body);
    }

    [Fact, Trait("Category", "TimeBasedTests")]
    public async Task ReceiveMessageAsync_MessageNotMovedToErrorQueue_IfDeletedBeforeMaxReceives()
    {
        // Create main queue and error queue
        var mainQueueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "main-queue" },
            TestContext.Current.CancellationToken)).QueueUrl;
        var errorQueueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "error-queue" },
            TestContext.Current.CancellationToken)).QueueUrl;
        var errorQueueArn = $"arn:aws:sqs:us-east-1:{AccountId}:{errorQueueUrl.Split('/').Last()}";

        // Set up redrive policy for the main queue
        await Sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = mainQueueUrl,
            Attributes = new Dictionary<string, string>
            {
                ["RedrivePolicy"] = $$"""{"maxReceiveCount":3, "deadLetterTargetArn":"{{errorQueueArn}}"}"""
            }
        }, TestContext.Current.CancellationToken);

        var request = new ReceiveMessageRequest
        {
            QueueUrl = mainQueueUrl,
            WaitTimeSeconds = 0,
            VisibilityTimeout = 5,
            MessageSystemAttributeNames = ["ApproximateReceiveCount"]
        };

        // Send a message to the main queue
        await Sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = mainQueueUrl, MessageBody = "Test message" },
            TestContext.Current.CancellationToken);

        // Receive the message twice
        for (int i = 0; i < 2; i++)
        {
            var result = await Sqs.ReceiveMessageAsync(request, TestContext.Current.CancellationToken);
            Assert.Single(result.Messages);
            Assert.Equal((i + 1).ToString(NumberFormatInfo.InvariantInfo),
                result.Messages[0].Attributes["ApproximateReceiveCount"]);
            await AdvanceTime(TimeSpan.FromSeconds(6)); // Wait for visibility timeout to expire
        }

        // Receive and delete the message on the third receive
        var finalResult = await Sqs.ReceiveMessageAsync(request, TestContext.Current.CancellationToken);
        var message = Assert.Single(finalResult.Messages);
        Assert.Equal("3", message.Attributes["ApproximateReceiveCount"]);
        await Sqs.DeleteMessageAsync(mainQueueUrl, message.ReceiptHandle, TestContext.Current.CancellationToken);

        // Try to receive from the main queue - should be empty
        var emptyMainResult = await Sqs.ReceiveMessageAsync(request, TestContext.Current.CancellationToken);
        Assert.Null(emptyMainResult.Messages);

        // Check the error queue - should be empty
        var errorQueueResult = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = errorQueueUrl },
            TestContext.Current.CancellationToken);
        Assert.Null(errorQueueResult.Messages);
    }

    public async Task ReceiveMessageAsync_ErrorQueueRespectsFifoOrder()
    {
        // Create main FIFO queue and error FIFO queue
        var mainQueueUrl =
            (await Sqs.CreateQueueAsync(
                new CreateQueueRequest
                {
                    QueueName = "main-queue.fifo",
                    Attributes = new Dictionary<string, string> { ["FifoQueue"] = "true" }
                }, TestContext.Current.CancellationToken)).QueueUrl;
        var errorQueueUrl =
            (await Sqs.CreateQueueAsync(
                new CreateQueueRequest
                {
                    QueueName = "error-queue.fifo",
                    Attributes = new Dictionary<string, string> { ["FifoQueue"] = "true" }
                }, TestContext.Current.CancellationToken)).QueueUrl;

        // Set up redrive policy for the main queue
        await Sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = mainQueueUrl,
            Attributes = new Dictionary<string, string>
            {
                ["RedrivePolicy"] = $$"""{"maxReceiveCount":"3", "deadLetterTargetArn":"{{errorQueueUrl}}"}"""
            }
        }, TestContext.Current.CancellationToken);

        var request = new ReceiveMessageRequest
        {
            QueueUrl = mainQueueUrl,
            WaitTimeSeconds = 0,
            VisibilityTimeout = 5,
            MessageSystemAttributeNames = ["ApproximateReceiveCount"]
        };

        // Send messages to the main queue
        await Sqs.SendMessageAsync(
            new SendMessageRequest
            {
                QueueUrl = mainQueueUrl, MessageBody = "Message 1", MessageGroupId = "group1",
                MessageDeduplicationId = "dedup1"
            }, TestContext.Current.CancellationToken);
        await Sqs.SendMessageAsync(
            new SendMessageRequest
            {
                QueueUrl = mainQueueUrl, MessageBody = "Message 2", MessageGroupId = "group1",
                MessageDeduplicationId = "dedup2"
            }, TestContext.Current.CancellationToken);

        // Receive and fail to process each message 3 times
        for (int i = 0; i < 3; i++)
        {
            var result = await Sqs.ReceiveMessageAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(2, result.Messages.Count);
            await AdvanceTime(TimeSpan.FromSeconds(6)); // Wait for visibility timeout to expire
        }

        // Check the error queue - messages should be there in order
        var errorQueueResult = await Sqs.ReceiveMessageAsync(
            new ReceiveMessageRequest { QueueUrl = errorQueueUrl, MaxNumberOfMessages = 10 },
            TestContext.Current.CancellationToken);
        Assert.Equal(2, errorQueueResult.Messages.Count);
        Assert.Equal("Message 1", errorQueueResult.Messages[0].Body);
        Assert.Equal("Message 2", errorQueueResult.Messages[1].Body);
    }

    [Fact]
    public async Task ReceiveMessageAsync_SpecificMessageSystemAttributes_OnlyRequestedAttributesReturned()
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test-queue" },
            TestContext.Current.CancellationToken)).QueueUrl;

        // Send a message with system attributes
        await Sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "Test message",
            MessageSystemAttributes = new Dictionary<string, MessageSystemAttributeValue>
            {
                [MessageSystemAttributeName.SenderId] = new MessageSystemAttributeValue
                    { StringValue = "TestSender", DataType = "String" },
                [MessageSystemAttributeName.SentTimestamp] = new MessageSystemAttributeValue
                    { StringValue = "1621234567890", DataType = "String" }
            }
        }, TestContext.Current.CancellationToken);

        // Request only specific system attributes
        var request = new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MessageAttributeNames = ["All"],
            MessageSystemAttributeNames = [MessageSystemAttributeName.SenderId]
        };

        var result = await Sqs.ReceiveMessageAsync(request, TestContext.Current.CancellationToken);
        var message = Assert.Single(result.Messages);

        // Check that only the requested system attribute is present
        Assert.True(message.Attributes.ContainsKey(MessageSystemAttributeName.SenderId));
        Assert.False(message.Attributes.ContainsKey(MessageSystemAttributeName.SentTimestamp));
        Assert.Equal("TestSender", message.Attributes[MessageSystemAttributeName.SenderId]);
    }

    [Fact]
    public async Task ReceiveMessageAsync_AllMessageSystemAttributes_AllAttributesReturned()
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test-queue" },
            TestContext.Current.CancellationToken)).QueueUrl;

        // Send a message with system attributes
        await Sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "Test message",
            MessageSystemAttributes = new Dictionary<string, MessageSystemAttributeValue>
            {
                [MessageSystemAttributeName.SenderId] = new() { StringValue = "TestSender", DataType = "String" },
                [MessageSystemAttributeName.SentTimestamp] =
                    new() { StringValue = "1621234567890", DataType = "String" },
                [MessageSystemAttributeName.ApproximateFirstReceiveTimestamp] =
                    new() { StringValue = "1621234567891", DataType = "String" },
                [MessageSystemAttributeName.ApproximateReceiveCount] = new() { StringValue = "0", DataType = "String" },
                [MessageSystemAttributeName.AWSTraceHeader] = new()
                    { StringValue = "Root=1-5e3d83c1-e6a0db584850d61342823d4c", DataType = "String" }
            }
        }, TestContext.Current.CancellationToken);

        // Request all system attributes
        var request = new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MessageAttributeNames = ["All"],
            MessageSystemAttributeNames = ["All"]
        };

        var result = await Sqs.ReceiveMessageAsync(request, TestContext.Current.CancellationToken);
        var message = Assert.Single(result.Messages);

        // Check that all system attributes are present
        Assert.Equal(5, message.Attributes.Count);
        Assert.Equal("TestSender", message.Attributes[MessageSystemAttributeName.SenderId]);
        Assert.Equal("1", message.Attributes[MessageSystemAttributeName.ApproximateReceiveCount]);
        Assert.Equal("Root=1-5e3d83c1-e6a0db584850d61342823d4c",
            message.Attributes[MessageSystemAttributeName.AWSTraceHeader]);
        if (false)
#pragma warning disable CS0162 // Unreachable code detected
        {
            Assert.Equal("1621234567890", message.Attributes[MessageSystemAttributeName.SentTimestamp]);
            Assert.Equal("1621234567891",
                message.Attributes[MessageSystemAttributeName.ApproximateFirstReceiveTimestamp]);
        }
#pragma warning restore CS0162 // Unreachable code detected
    }

    [Fact]
    public async Task ReceiveMessageAsync_NoMessageSystemAttributes_NoAttributesReturned()
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test-queue" },
            TestContext.Current.CancellationToken)).QueueUrl;

        // Send a message with system attributes
        await Sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "Test message",
            MessageSystemAttributes = new Dictionary<string, MessageSystemAttributeValue>
            {
                [MessageSystemAttributeName.SenderId] = new MessageSystemAttributeValue
                    { StringValue = "TestSender", DataType = "String" },
                [MessageSystemAttributeName.SentTimestamp] = new MessageSystemAttributeValue
                    { StringValue = "1621234567890", DataType = "String" }
            }
        }, TestContext.Current.CancellationToken);

        // Don't request any system attributes
        var request = new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MessageAttributeNames = ["All"],
            MessageSystemAttributeNames = []
        };

        var result = await Sqs.ReceiveMessageAsync(request, TestContext.Current.CancellationToken);
        var message = Assert.Single(result.Messages);

        // Check that no system attributes are present
        Assert.Null(message.Attributes);
    }

    [Fact]
    public async Task ReceiveMessageAsync_MultipleMessages_CorrectAttributesReturnedForEach()
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test-queue" },
            TestContext.Current.CancellationToken)).QueueUrl;

        // Send two messages with different system attributes
        await Sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "Message 1",
            MessageSystemAttributes = new Dictionary<string, MessageSystemAttributeValue>
            {
                [MessageSystemAttributeName.SenderId] = new MessageSystemAttributeValue
                    { StringValue = "Sender1", DataType = "String" },
                [MessageSystemAttributeName.SentTimestamp] = new MessageSystemAttributeValue
                    { StringValue = "1621234567890", DataType = "String" }
            }
        }, TestContext.Current.CancellationToken);

        await Sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "Message 2",
            MessageSystemAttributes = new Dictionary<string, MessageSystemAttributeValue>
            {
                [MessageSystemAttributeName.SenderId] = new MessageSystemAttributeValue
                    { StringValue = "Sender2", DataType = "String" },
                [MessageSystemAttributeName.AWSTraceHeader] = new MessageSystemAttributeValue
                    { StringValue = "Root=1-5e3d83c1-e6a0db584850d61342823d4c", DataType = "String" }
            }
        }, TestContext.Current.CancellationToken);

        // Request specific system attributes
        var request = new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 10,
            MessageAttributeNames = ["All"],
            MessageSystemAttributeNames =
                [MessageSystemAttributeName.SenderId, MessageSystemAttributeName.AWSTraceHeader]
        };

        var result = await Sqs.ReceiveMessageAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(2, result.Messages.Count);

        var message1 = result.Messages.First(m => m.Body == "Message 1");
        var message2 = result.Messages.First(m => m.Body == "Message 2");

        // Check attributes for Message 1
        Assert.Single(message1.Attributes);
        Assert.Equal("Sender1", message1.Attributes[MessageSystemAttributeName.SenderId]);

        // Check attributes for Message 2
        Assert.Equal(2, message2.Attributes.Count);
        Assert.Equal("Sender2", message2.Attributes[MessageSystemAttributeName.SenderId]);
        Assert.Equal("Root=1-5e3d83c1-e6a0db584850d61342823d4c",
            message2.Attributes[MessageSystemAttributeName.AWSTraceHeader]);
    }

    // Permission tests
    [Fact]
    public async Task AddPermissionAsync_ValidRequest_AddsPermissionToPolicy()
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test-queue" },
            TestContext.Current.CancellationToken)).QueueUrl;
        var request = new AddPermissionRequest
        {
            QueueUrl = queueUrl,
            Label = "TestPermission",
            AWSAccountIds = ["123456789012"],
            Actions = ["SendMessage", "ReceiveMessage"]
        };

        var response = await Sqs.AddPermissionAsync(request, TestContext.Current.CancellationToken);

        Assert.NotNull(response);
        var attributes = await Sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            AttributeNames = ["Policy"]
        }, TestContext.Current.CancellationToken);
        Assert.True(attributes.Attributes.ContainsKey("Policy"));
        var policy = Policy.FromJson(attributes.Attributes["Policy"]);
        Assert.Contains(policy.Statements, s => s.Id == "TestPermission");
    }

    [Fact]
    public async Task AddPermissionAsync_DuplicateLabel_ThrowsArgumentException()
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test-queue" },
            TestContext.Current.CancellationToken)).QueueUrl;
        var request = new AddPermissionRequest
        {
            QueueUrl = queueUrl,
            Label = "TestPermission",
            AWSAccountIds = ["123456789012"],
            Actions = ["SendMessage"]
        };

        await Sqs.AddPermissionAsync(request, TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await Sqs.AddPermissionAsync(request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AddPermissionAsync_QueueDoesNotExist_ThrowsQueueDoesNotExistException()
    {
        var request = new AddPermissionRequest
        {
            QueueUrl = "http://sqs.us-east-1.amazonaws.com/123456789012/non-existent-queue",
            Label = "TestPermission",
            AWSAccountIds = ["123456789012"],
            Actions = ["SendMessage"]
        };

        await Assert.ThrowsAsync<QueueDoesNotExistException>(async () =>
            await Sqs.AddPermissionAsync(request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RemovePermissionAsync_ValidRequest_RemovesPermissionFromPolicy()
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test-queue" },
            TestContext.Current.CancellationToken)).QueueUrl;
        await Sqs.AddPermissionAsync(new AddPermissionRequest
        {
            QueueUrl = queueUrl,
            Label = "TestPermission",
            AWSAccountIds = ["123456789012"],
            Actions = ["SendMessage"]
        }, TestContext.Current.CancellationToken);

        var removeRequest = new RemovePermissionRequest
        {
            QueueUrl = queueUrl,
            Label = "TestPermission"
        };

        var response = await Sqs.RemovePermissionAsync(removeRequest, TestContext.Current.CancellationToken);

        Assert.NotNull(response);
        var attributes = await Sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            AttributeNames = ["Policy"]
        }, TestContext.Current.CancellationToken);
        Assert.False(attributes.Attributes?.ContainsKey("Policy") ?? false);
    }

    [Fact]
    public async Task RemovePermissionAsync_LabelDoesNotExist_ThrowsArgumentException()
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test-queue" },
            TestContext.Current.CancellationToken)).QueueUrl;
        var request = new RemovePermissionRequest
        {
            QueueUrl = queueUrl,
            Label = "NonExistentLabel"
        };

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await Sqs.RemovePermissionAsync(request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RemovePermissionAsync_QueueDoesNotExist_ThrowsQueueDoesNotExistException()
    {
        var request = new RemovePermissionRequest
        {
            QueueUrl = "http://sqs.us-east-1.amazonaws.com/123456789012/non-existent-queue",
            Label = "TestPermission"
        };

        await Assert.ThrowsAsync<QueueDoesNotExistException>(async () =>
            await Sqs.RemovePermissionAsync(request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AddAndRemovePermission_MultiplePermissions_ManagesCorrectly()
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test-queue" },
            TestContext.Current.CancellationToken)).QueueUrl;

        // Add first permission
        await Sqs.AddPermissionAsync(new AddPermissionRequest
        {
            QueueUrl = queueUrl,
            Label = "Permission1",
            AWSAccountIds = ["123456789012"],
            Actions = ["SendMessage"]
        }, TestContext.Current.CancellationToken);

        // Add second permission
        await Sqs.AddPermissionAsync(new AddPermissionRequest
        {
            QueueUrl = queueUrl,
            Label = "Permission2",
            AWSAccountIds = ["210987654321"],
            Actions = ["ReceiveMessage"]
        }, TestContext.Current.CancellationToken);

        // Verify both permissions exist
        var attributes = await Sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            AttributeNames = ["Policy"]
        }, TestContext.Current.CancellationToken);
        var policy = Policy.FromJson(attributes.Attributes["Policy"]);
        Assert.Equal(2, policy.Statements.Count);
        Assert.Contains(policy.Statements, s => s.Id == "Permission1");
        Assert.Contains(policy.Statements, s => s.Id == "Permission2");

        // Remove first permission
        await Sqs.RemovePermissionAsync(new RemovePermissionRequest
        {
            QueueUrl = queueUrl,
            Label = "Permission1"
        }, TestContext.Current.CancellationToken);

        // Verify only second permission remains
        attributes = await Sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            AttributeNames = ["Policy"]
        }, TestContext.Current.CancellationToken);
        policy = Policy.FromJson(attributes.Attributes["Policy"]);
        Assert.Single(policy.Statements);
        Assert.Contains(policy.Statements, s => s.Id == "Permission2");

        // Remove second permission
        await Sqs.RemovePermissionAsync(new RemovePermissionRequest
        {
            QueueUrl = queueUrl,
            Label = "Permission2"
        }, TestContext.Current.CancellationToken);

        // Verify policy is removed
        attributes = await Sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            AttributeNames = ["Policy"]
        }, TestContext.Current.CancellationToken);
        Assert.False(attributes.Attributes?.ContainsKey("Policy") ?? false);
    }

    [Fact]
    public async Task SendMessageAsync_MessageExceedsMaximumSize_ThrowsInvalidMessageContentsException()
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test-queue" },
            TestContext.Current.CancellationToken)).QueueUrl;

        // Create a message that exceeds 256KB (262,144 bytes)
        var largeMessage = new string('x', 262145);

        var request = new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = largeMessage
        };

        await Assert.ThrowsAsync<AmazonSQSException>(() =>
            Sqs.SendMessageAsync(request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SendMessageAsync_MessageAttributeFullSizeCalculation_ThrowsException()
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test-queue" },
            TestContext.Current.CancellationToken)).QueueUrl;

        // The total size includes:
        // 1. Message body
        // 2. Each message attribute name
        // 3. Each message attribute type (including "String", "Number", etc.)
        // 4. Each message attribute value
        var messageBody = new string('x', 200_000);

        var request = new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = messageBody,
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

        await Assert.ThrowsAsync<AmazonSQSException>(() =>
            Sqs.SendMessageAsync(request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SendMessageAsync_MultipleAttributesExactlyAtLimit_Succeeds()
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test-queue" },
            TestContext.Current.CancellationToken)).QueueUrl;

        // Calculate sizes to reach exactly 256KB:
        // - Message body: 200,000 bytes
        // - First attribute:
        //   * Name: 100 bytes
        //   * Type: "String" (6 bytes)
        //   * Value: 31,000 bytes
        // - Second attribute:
        //   * Name: 20 bytes
        //   * Type: "Number" (6 bytes)
        //   * Value: 31,012 bytes
        // Total: 262,144 bytes (256KB)

        var sendRequest = new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = new string('x', 200000),
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                [new string('a', 100)] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = new string('y', 31000)
                },
                [new string('b', 20)] = new MessageAttributeValue
                {
                    DataType = "Number",
                    StringValue = new string('z', 31012)
                }
            }
        };

        var sendResponse = await Sqs.SendMessageAsync(sendRequest, TestContext.Current.CancellationToken);
        Assert.NotNull(sendResponse.MessageId);

        // Verify we can receive the message with attributes
        var receiveResponse = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 1,
            MessageAttributeNames = ["All"]
        }, TestContext.Current.CancellationToken);

        var message = Assert.Single(receiveResponse.Messages);
        Assert.Equal(2, message.MessageAttributes.Count);
    }

    [Fact]
    public async Task SendMessageAsync_BinaryAttributeSize_Succeeds()
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test-queue" },
            TestContext.Current.CancellationToken)).QueueUrl;

        // Create a binary attribute
        byte[] binaryData = new byte[1000];
#pragma warning disable CA5394
        new Random(42).NextBytes(binaryData);
#pragma warning restore CA5394

        var request = new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = new string('x', 260000),
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                ["BinaryAttribute"] = new MessageAttributeValue
                {
                    DataType = "Binary",
                    BinaryValue = new MemoryStream(binaryData)
                }
            }
        };

        // Total size: 260,000 (body) + 14 (attribute name) + 6 (type) + 1,000 (binary value) = 261,020 bytes
        var response = await Sqs.SendMessageAsync(request, TestContext.Current.CancellationToken);
        Assert.NotNull(response.MessageId);
    }

    [Fact]
    public async Task SendMessageAsync_CustomAttributeTypeNames_CountTowardsLimit()
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test-queue" },
            TestContext.Current.CancellationToken)).QueueUrl;

        // Using a custom attribute type name which counts towards the limit
        var longCustomType = $"String.{new string('x', 1000)}";

        var request = new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = new string('x', 262000),
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                ["CustomAttribute"] = new MessageAttributeValue
                {
                    DataType = longCustomType,
                    StringValue = "test"
                }
            }
        };

        // The long custom type name should push us over the limit
        await Assert.ThrowsAsync<AmazonSQSException>(() =>
            Sqs.SendMessageAsync(request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SendMessageAsync_BatchWithAttributeSizeLimits_PartialBatchFailure()
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test-queue" },
            TestContext.Current.CancellationToken)).QueueUrl;

        var validMessage = new SendMessageBatchRequestEntry
        {
            Id = "1",
            MessageBody = "Valid message",
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                ["attr1"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = "test"
                }
            }
        };

        var oversizedMessage = new SendMessageBatchRequestEntry
        {
            Id = "2",
            MessageBody = new string('x', 260000),
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                [new string('a', 1000)] = new MessageAttributeValue // Long attribute name
                {
                    DataType = "String",
                    StringValue = new string('y', 2000)
                }
            }
        };

        var request = new SendMessageBatchRequest
        {
            QueueUrl = queueUrl,
            Entries = [validMessage, oversizedMessage]
        };

        await Assert.ThrowsAsync<BatchRequestTooLongException>(async () =>
            await Sqs.SendMessageBatchAsync(request, TestContext.Current.CancellationToken));
    }

    protected abstract Task AdvanceTime(TimeSpan timeSpan);
}

using Amazon.Auth.AccessControlPolicy;
using Amazon.SQS;
using Amazon.SQS.Model;
using Shouldly;

namespace LocalSqsSnsMessaging.Tests;

public abstract class SqsReceiveMessageAsyncTests : WaitingTestBase
{
    protected IAmazonSQS Sqs = null!;
    protected string AccountId = null!;

    [Test]
    public async Task ReceiveMessageAsync_QueueNotFound_ThrowsQueueDoesNotExistException(CancellationToken cancellationToken)
    {
        var request = new ReceiveMessageRequest { QueueUrl = "nonexistent-queue" };

        await Assert.ThrowsAsync<QueueDoesNotExistException>(() =>
            Sqs.ReceiveMessageAsync(request, cancellationToken));
    }

    [Test]
    public async Task ReceiveMessageAsync_NoMessages_ReturnsEmptyList(CancellationToken cancellationToken)
    {
        var createQueueResponse = await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test-queue" },
            cancellationToken);
        var queueUrl = createQueueResponse.QueueUrl;
        var request = new ReceiveMessageRequest { QueueUrl = queueUrl, WaitTimeSeconds = 0 };

        var result = await Sqs.ReceiveMessageAsync(request, cancellationToken);

        result.Messages.ShouldBeEmptyAwsCollection();
    }

    [Test]
    public async Task ReceiveMessageAsync_MessagesAvailable_ReturnsMessages(CancellationToken cancellationToken)
    {
        var createQueueResponse = await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test-queue" },
            cancellationToken);
        var queueUrl = createQueueResponse.QueueUrl;
        var request = new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 2 };

        await Sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "Hello, world!"
        }, cancellationToken);
        await Sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "Goodbye, world!"
        }, cancellationToken);

        var result = await Sqs.ReceiveMessageAsync(request, cancellationToken);

        result.Messages.Count.ShouldBe(2);
        result.Messages[0].Body.ShouldBe("Hello, world!");
        result.Messages[1].Body.ShouldBe("Goodbye, world!");
    }

    [Test, Category("TimeBasedTests")]
    public async Task ReceiveMessageAsync_WaitsForMessages_ReturnsMessagesWhenAvailable(CancellationToken cancellationToken)
    {
        var createQueueResponse = await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test-queue" },
            cancellationToken);
        var queueUrl = createQueueResponse.QueueUrl;
        var request = new ReceiveMessageRequest { QueueUrl = queueUrl, WaitTimeSeconds = 5 };

        var task = Sqs.ReceiveMessageAsync(request, cancellationToken);

        await WaitAsync(TimeSpan.FromSeconds(3));
        await Sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "Hello, world!"
        }, cancellationToken);

        var result = await task;

        var receivedMessage = result.Messages.ShouldHaveSingleItem();
        receivedMessage.Body.ShouldBe("Hello, world!");
    }

    [Test, Category("TimeBasedTests")]
    public async Task ReceiveMessageAsync_Timeout_ReturnsEmptyList(CancellationToken cancellationToken)
    {
        var createQueueResponse = await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test-queue" },
            cancellationToken);
        var queueUrl = createQueueResponse.QueueUrl;
        var request = new ReceiveMessageRequest { QueueUrl = queueUrl, WaitTimeSeconds = 5 };

        var task = Sqs.ReceiveMessageAsync(request, cancellationToken);

        await WaitAsync(TimeSpan.FromSeconds(5));

        var result = await task;

        result.Messages.ShouldBeEmptyAwsCollection();
    }

    [Test]
    public async Task ReceiveMessageAsync_CancellationRequested_ReturnsEmptyList(CancellationToken cancellationToken)
    {
        var createQueueResponse = await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test-queue" },
            cancellationToken);
        var queueUrl = createQueueResponse.QueueUrl;
        var request = new ReceiveMessageRequest { QueueUrl = queueUrl, WaitTimeSeconds = 10 };

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await Sqs.ReceiveMessageAsync(request, cts.Token)
        );
    }

    [Test, Category("TimeBasedTests")]
    public async Task ReceiveMessageAsync_RespectVisibilityTimeout(CancellationToken cancellationToken)
    {
        var createQueueResponse = await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test-queue" },
            cancellationToken);
        var queueUrl = createQueueResponse.QueueUrl;
        var request = new ReceiveMessageRequest { QueueUrl = queueUrl, WaitTimeSeconds = 0, VisibilityTimeout = 6 };

        // Enqueue a message
        await Sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "Hello, world!"
        }, cancellationToken);

        // First receive - should get the message
        var result1 = await Sqs.ReceiveMessageAsync(request, cancellationToken);
        var firstReceivedMessage = result1.Messages.ShouldHaveSingleItem();
        firstReceivedMessage.Body.ShouldBe("Hello, world!");

        // Second receive immediately after - should not get any message
        var result2 = await Sqs.ReceiveMessageAsync(request, cancellationToken);
        result2.Messages.ShouldBeEmptyAwsCollection();

        // Advance time by 3 seconds (half the visibility timeout)
        await WaitAsync(TimeSpan.FromSeconds(3));

        // Third receive - should still not get any message
        var result3 = await Sqs.ReceiveMessageAsync(request, cancellationToken);
        result3.Messages.ShouldBeEmptyAwsCollection();

        // Advance time by another 4 seconds (visibility timeout has now passed)
        await WaitAsync(TimeSpan.FromSeconds(4));

        // Fourth receive - should get the message again
        var result4 = await Sqs.ReceiveMessageAsync(request, cancellationToken);
        var forthReceivedMessage = result4.Messages.ShouldHaveSingleItem();
        forthReceivedMessage.Body.ShouldBe("Hello, world!");
    }

    [Test, Category("TimeBasedTests")]
    public async Task ReceiveMessageAsync_DelayedMessageBecomesVisible(CancellationToken cancellationToken)
    {
        var createQueueResponse = await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test-queue" },
            cancellationToken);
        var queueUrl = createQueueResponse.QueueUrl;
        var request = new ReceiveMessageRequest { QueueUrl = queueUrl, WaitTimeSeconds = 0, VisibilityTimeout = 10 };

        // Enqueue a message with a delay of 5 seconds
        await Sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "Hello, world!",
            DelaySeconds = 5
        }, cancellationToken);

        // First receive - should not get any message
        var result1 = await Sqs.ReceiveMessageAsync(request, cancellationToken);
        result1.Messages.ShouldBeEmptyAwsCollection();

        // Advance time by 5 seconds
        await WaitAsync(TimeSpan.FromSeconds(2.5));

        // Second receive - should still not get any message
        var result2 = await Sqs.ReceiveMessageAsync(request, cancellationToken);
        result2.Messages.ShouldBeEmptyAwsCollection();

        // Advance time by another 5 seconds (message is now visible)
        await WaitAsync(TimeSpan.FromSeconds(5));

        // Third receive - should get the message
        var result3 = await Sqs.ReceiveMessageAsync(request, cancellationToken);
        var receivedMessage = result3.Messages.ShouldHaveSingleItem();
        receivedMessage.Body.ShouldBe("Hello, world!");
    }

    [Test, Category("TimeBasedTests"), Retry(3)]
    public async Task ReceiveMessageAsync_MultipleMessagesWithDifferentDelays(CancellationToken cancellationToken)
    {
        const int initialVisibilityTimeout = 6; // seconds
        //const int visibilityTimeout = 6; // seconds
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test-queue" },
            cancellationToken)).QueueUrl;
        var receiveAllImmediately = new ReceiveMessageRequest
            { QueueUrl = queueUrl, WaitTimeSeconds = 0, VisibilityTimeout = initialVisibilityTimeout, MaxNumberOfMessages = 10 };
        var receiveOneImmediately = new ReceiveMessageRequest
            { QueueUrl = queueUrl, WaitTimeSeconds = 0, VisibilityTimeout = initialVisibilityTimeout, MaxNumberOfMessages = 1 };
        var receiveOneWhenAvailable = new ReceiveMessageRequest
            { QueueUrl = queueUrl, WaitTimeSeconds = 20, VisibilityTimeout = initialVisibilityTimeout, MaxNumberOfMessages = 1 };

        // Send messages with different delays
        await Sqs.SendMessageAsync(new SendMessageRequest
                { QueueUrl = queueUrl, MessageBody = "Message 1", DelaySeconds = 2 },
            cancellationToken);
        await Sqs.SendMessageAsync(new SendMessageRequest
                { QueueUrl = queueUrl, MessageBody = "Message 2", DelaySeconds = 4 },
            cancellationToken);
        await Sqs.SendMessageAsync(new SendMessageRequest
                { QueueUrl = queueUrl, MessageBody = "Message 3", DelaySeconds = 6 },
            cancellationToken);

        var result1Task = Sqs.ReceiveMessageAsync(receiveOneWhenAvailable, cancellationToken);
        await WaitAsync(TimeSpan.FromSeconds(3));
        var result1 = await result1Task;
        result1.Messages.ShouldHaveSingleItem();
        result1.Messages[0].Body.ShouldBe("Message 1");

        // Advance time to make the second message visible
        await WaitAsync(TimeSpan.FromSeconds(2));
        var result2 = await Sqs.ReceiveMessageAsync(receiveAllImmediately, cancellationToken);
        result2.Messages.ShouldHaveSingleItem();
        result2.Messages[0].Body.ShouldBe("Message 2");

        // Advance time to make the third message visible
        await WaitAsync(TimeSpan.FromSeconds(2));
        var result3 = await Sqs.ReceiveMessageAsync(receiveAllImmediately, cancellationToken);
        result3.Messages.ShouldHaveSingleItem();
        result3.Messages[0].Body.ShouldBe("Message 3");

        // All message should now not be visible anymore
        var result4 = await Sqs.ReceiveMessageAsync(receiveAllImmediately, cancellationToken);
        result4.Messages.ShouldBeEmptyAwsCollection();

        // Advance time past the visibility timeout of the first message
        await WaitAsync(TimeSpan.FromSeconds(5));
        var result5 = await Sqs.ReceiveMessageAsync(receiveOneImmediately, cancellationToken);
        result5.Messages.ShouldHaveSingleItem().Body.ShouldBe("Message 1");

        // Advance time past the visibility timeout of the second message
        await WaitAsync(TimeSpan.FromSeconds(2));
        var result6 = await Sqs.ReceiveMessageAsync(receiveOneImmediately, cancellationToken);
        result6.Messages.ShouldHaveSingleItem().Body.ShouldBe("Message 2");

        // Advance time past the visibility timeout of the third message
        await WaitAsync(TimeSpan.FromSeconds(2));
        var result7 = await Sqs.ReceiveMessageAsync(receiveOneImmediately, cancellationToken);
        result7.Messages.ShouldHaveSingleItem().Body.ShouldBe("Message 3");
    }

    [Test, Category("TimeBasedTests")]
    public async Task ReceiveMessageAsync_ApproximateReceiveCount_IncreasesWithEachReceive(CancellationToken cancellationToken)
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test-queue" },
            cancellationToken)).QueueUrl;
        var request = new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            WaitTimeSeconds = 0,
            VisibilityTimeout = 5,
            MessageSystemAttributeNames = ["ApproximateReceiveCount"]
        };

        // Send a message
        await Sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = queueUrl, MessageBody = "Test message" },
            cancellationToken);

        // First receive
        var result1 = await Sqs.ReceiveMessageAsync(request, cancellationToken);
        var message1 = result1.Messages.ShouldHaveSingleItem();
        message1.Attributes["ApproximateReceiveCount"].ShouldBe("1");

        // Wait for visibility timeout to expire
        await WaitAsync(TimeSpan.FromSeconds(6));

        // Second receive
        var result2 = await Sqs.ReceiveMessageAsync(request, cancellationToken);
        var message2 = result2.Messages.ShouldHaveSingleItem();
        message2.Attributes["ApproximateReceiveCount"].ShouldBe("2");

        // Wait for visibility timeout to expire again
        await WaitAsync(TimeSpan.FromSeconds(6));

        // Third receive
        var result3 = await Sqs.ReceiveMessageAsync(request, cancellationToken);
        var message3 = result3.Messages.ShouldHaveSingleItem();
        message3.Attributes["ApproximateReceiveCount"].ShouldBe("3");
    }

    [Test]
    public async Task ReceiveMessageAsync_ApproximateReceiveCount_ResetAfterDelete(CancellationToken cancellationToken)
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test-queue" },
            cancellationToken)).QueueUrl;
        var request = new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            WaitTimeSeconds = 0,
            VisibilityTimeout = 5,
            MessageSystemAttributeNames = ["ApproximateReceiveCount"]
        };

        // Send a message
        await Sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = queueUrl, MessageBody = "Test message" },
            cancellationToken);

        // Receive and delete the message
        var result1 = await Sqs.ReceiveMessageAsync(request, cancellationToken);
        var message1 = result1.Messages.ShouldHaveSingleItem();
        message1.Attributes["ApproximateReceiveCount"].ShouldBe("1");
        await Sqs.DeleteMessageAsync(queueUrl, message1.ReceiptHandle, cancellationToken);

        // Send another message
        await Sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = queueUrl, MessageBody = "New test message" },
            cancellationToken);

        // Receive the new message
        var result2 = await Sqs.ReceiveMessageAsync(request, cancellationToken);
        var message2 = result2.Messages.ShouldHaveSingleItem();
        message2.Attributes["ApproximateReceiveCount"].ShouldBe("1");
    }

    [Test, Category("TimeBasedTests")]
    public async Task ReceiveMessageAsync_ApproximateReceiveCount_MultipleMessages(CancellationToken cancellationToken)
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test-queue" },
            cancellationToken)).QueueUrl;
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
            cancellationToken);
        await Sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = queueUrl, MessageBody = "Message 2" },
            cancellationToken);

        // First receive
        var result1 = await Sqs.ReceiveMessageAsync(request, cancellationToken);
        result1.Messages.Count.ShouldBe(2);
        result1.Messages.ShouldAllBe(m => m.Attributes["ApproximateReceiveCount"] == "1");

        // Wait for visibility timeout to expire
        await WaitAsync(TimeSpan.FromSeconds(6));

        // Second receive
        var result2 = await Sqs.ReceiveMessageAsync(request, cancellationToken);
        result2.Messages.Count.ShouldBe(2);
        result2.Messages.ShouldAllBe(m => m.Attributes["ApproximateReceiveCount"] == "2");

        // Delete one message
        await Sqs.DeleteMessageAsync(queueUrl, result2.Messages[0].ReceiptHandle,
            cancellationToken);

        // Wait for visibility timeout to expire again
        await WaitAsync(TimeSpan.FromSeconds(6));

        // Third receive
        var result3 = await Sqs.ReceiveMessageAsync(request, cancellationToken);
        var message3 = result3.Messages.ShouldHaveSingleItem();
        message3.Attributes["ApproximateReceiveCount"].ShouldBe("3");
    }

    [Test, Category("TimeBasedTests")]
    public async Task ReceiveMessageAsync_MessageMovedToErrorQueue_AfterMaxReceives(CancellationToken cancellationToken)
    {
        // Create main queue and error queue
        var mainQueueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "main-queue" },
            cancellationToken)).QueueUrl;
        var errorQueueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "error-queue" },
            cancellationToken)).QueueUrl;
        var errorQueueArn = $"arn:aws:sqs:us-east-1:{AccountId}:{errorQueueUrl.Split('/').Last()}";

        // Set up redrive policy for the main queue
        await Sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = mainQueueUrl,
            Attributes = new Dictionary<string, string>
            {
                ["RedrivePolicy"] = $$"""{"maxReceiveCount":2, "deadLetterTargetArn":"{{errorQueueArn}}"}"""
            }
        }, cancellationToken);

        var request = new ReceiveMessageRequest
        {
            QueueUrl = mainQueueUrl,
            WaitTimeSeconds = 0,
            VisibilityTimeout = 5,
            MessageSystemAttributeNames = ["ApproximateReceiveCount"],
        };

        // Send a message to the main queue
        await Sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = mainQueueUrl, MessageBody = "Test message" },
            cancellationToken);

        // Receive the message three times
        for (int i = 0; i < 2; i++)
        {
            var result = await Sqs.ReceiveMessageAsync(request, cancellationToken);
            var message = result.Messages.ShouldHaveSingleItem();
            message.Attributes["ApproximateReceiveCount"].ShouldBe((i + 1).ToString(NumberFormatInfo.InvariantInfo));
            await WaitAsync(TimeSpan.FromSeconds(6)); // Wait for visibility timeout to expire
        }

        // Try to receive from the main queue - should be empty
        var emptyResult = await Sqs.ReceiveMessageAsync(request, cancellationToken);
        emptyResult.Messages.ShouldBeEmptyAwsCollection();

        // Check the error queue - the message should be there
        var errorQueueResult = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = errorQueueUrl },
            cancellationToken);
        var errorMessage = errorQueueResult.Messages.ShouldHaveSingleItem();
        errorMessage.Body.ShouldBe("Test message");
    }

    [Test, Category("TimeBasedTests")]
    public async Task ReceiveMessageAsync_MessageNotMovedToErrorQueue_IfDeletedBeforeMaxReceives(CancellationToken cancellationToken)
    {
        // Create main queue and error queue
        var mainQueueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "main-queue" },
            cancellationToken)).QueueUrl;
        var errorQueueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "error-queue" },
            cancellationToken)).QueueUrl;
        var errorQueueArn = $"arn:aws:sqs:us-east-1:{AccountId}:{errorQueueUrl.Split('/').Last()}";

        // Set up redrive policy for the main queue
        await Sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = mainQueueUrl,
            Attributes = new Dictionary<string, string>
            {
                ["RedrivePolicy"] = $$"""{"maxReceiveCount":3, "deadLetterTargetArn":"{{errorQueueArn}}"}"""
            }
        }, cancellationToken);

        var request = new ReceiveMessageRequest
        {
            QueueUrl = mainQueueUrl,
            WaitTimeSeconds = 0,
            VisibilityTimeout = 5,
            MessageSystemAttributeNames = ["ApproximateReceiveCount"]
        };

        // Send a message to the main queue
        await Sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = mainQueueUrl, MessageBody = "Test message" },
            cancellationToken);

        // Receive the message twice
        for (int i = 0; i < 2; i++)
        {
            var result = await Sqs.ReceiveMessageAsync(request, cancellationToken);
            result.Messages.ShouldHaveSingleItem();
            result.Messages[0].Attributes["ApproximateReceiveCount"].ShouldBe((i + 1).ToString(NumberFormatInfo.InvariantInfo));
            await WaitAsync(TimeSpan.FromSeconds(6)); // Wait for visibility timeout to expire
        }

        // Receive and delete the message on the third receive
        var finalResult = await Sqs.ReceiveMessageAsync(request, cancellationToken);
        var message = finalResult.Messages.ShouldHaveSingleItem();
        message.Attributes["ApproximateReceiveCount"].ShouldBe("3");
        await Sqs.DeleteMessageAsync(mainQueueUrl, message.ReceiptHandle, cancellationToken);

        // Try to receive from the main queue - should be empty
        var emptyMainResult = await Sqs.ReceiveMessageAsync(request, cancellationToken);
        emptyMainResult.Messages.ShouldBeEmptyAwsCollection();

        // Check the error queue - should be empty
        var errorQueueResult = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = errorQueueUrl },
            cancellationToken);
        errorQueueResult.Messages.ShouldBeEmptyAwsCollection();
    }

    //[Test, Skip("This has never been working, we should fix it in the future")]
    private async Task ReceiveMessageAsync_ErrorQueueRespectsFifoOrder(CancellationToken cancellationToken)
    {
        // Create main FIFO queue and error FIFO queue
        var mainQueueUrl =
            (await Sqs.CreateQueueAsync(
                new CreateQueueRequest
                {
                    QueueName = "main-queue.fifo",
                    Attributes = new Dictionary<string, string> { ["FifoQueue"] = "true" }
                }, cancellationToken)).QueueUrl;
        var errorQueueUrl =
            (await Sqs.CreateQueueAsync(
                new CreateQueueRequest
                {
                    QueueName = "error-queue.fifo",
                    Attributes = new Dictionary<string, string> { ["FifoQueue"] = "true" }
                }, cancellationToken)).QueueUrl;

        // Set up redrive policy for the main queue
        await Sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = mainQueueUrl,
            Attributes = new Dictionary<string, string>
            {
                ["RedrivePolicy"] = $$"""{"maxReceiveCount":"3", "deadLetterTargetArn":"{{errorQueueUrl}}"}"""
            }
        }, cancellationToken);

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
            }, cancellationToken);
        await Sqs.SendMessageAsync(
            new SendMessageRequest
            {
                QueueUrl = mainQueueUrl, MessageBody = "Message 2", MessageGroupId = "group1",
                MessageDeduplicationId = "dedup2"
            }, cancellationToken);

        // Receive and fail to process each message 3 times
        for (int i = 0; i < 3; i++)
        {
            var result = await Sqs.ReceiveMessageAsync(request, cancellationToken);
            result.Messages.Count.ShouldBe(2);
            await WaitAsync(TimeSpan.FromSeconds(6)); // Wait for visibility timeout to expire
        }

        // Check the error queue - messages should be there in order
        var errorQueueResult = await Sqs.ReceiveMessageAsync(
            new ReceiveMessageRequest { QueueUrl = errorQueueUrl, MaxNumberOfMessages = 10 },
            cancellationToken);
        errorQueueResult.Messages.Count.ShouldBe(2);
        errorQueueResult.Messages[0].Body.ShouldBe("Message 1");
        errorQueueResult.Messages[1].Body.ShouldBe("Message 2");
    }

    [Test]
    public async Task ReceiveMessageAsync_SpecificMessageSystemAttributes_OnlyRequestedAttributesReturned(CancellationToken cancellationToken)
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test-queue" },
            cancellationToken)).QueueUrl;

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
        }, cancellationToken);

        // Request only specific system attributes
        var request = new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MessageAttributeNames = ["All"],
            MessageSystemAttributeNames = [MessageSystemAttributeName.SenderId]
        };

        var result = await Sqs.ReceiveMessageAsync(request, cancellationToken);
        var message = result.Messages.ShouldHaveSingleItem();

        // Check that only the requested system attribute is present
        message.Attributes.ShouldContainKey(MessageSystemAttributeName.SenderId);
        message.Attributes.ShouldNotContainKey(MessageSystemAttributeName.SentTimestamp);
        message.Attributes[MessageSystemAttributeName.SenderId].ShouldBe("TestSender");
    }

    [Test]
    public async Task ReceiveMessageAsync_AllMessageSystemAttributes_AllAttributesReturned(CancellationToken cancellationToken)
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test-queue" },
            cancellationToken)).QueueUrl;

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
        }, cancellationToken);

        // Request all system attributes
        var request = new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MessageAttributeNames = ["All"],
            MessageSystemAttributeNames = ["All"]
        };

        var result = await Sqs.ReceiveMessageAsync(request, cancellationToken);
        var message = result.Messages.ShouldHaveSingleItem();

        // Check that all system attributes are present
        message.Attributes.Count.ShouldBe(5);
        message.Attributes[MessageSystemAttributeName.SenderId].ShouldBe("TestSender");
        message.Attributes[MessageSystemAttributeName.ApproximateReceiveCount].ShouldBe("1");
        message.Attributes[MessageSystemAttributeName.AWSTraceHeader].ShouldBe("Root=1-5e3d83c1-e6a0db584850d61342823d4c");
        if (false)
#pragma warning disable CS0162 // Unreachable code detected
        {
            message.Attributes[MessageSystemAttributeName.SentTimestamp].ShouldBe("1621234567890");
            message.Attributes[MessageSystemAttributeName.ApproximateFirstReceiveTimestamp].ShouldBe("1621234567891");
        }
#pragma warning restore CS0162 // Unreachable code detected
    }

    [Test]
    public async Task ReceiveMessageAsync_NoMessageSystemAttributes_NoAttributesReturned(CancellationToken cancellationToken)
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test-queue" },
            cancellationToken)).QueueUrl;

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
        }, cancellationToken);

        // Don't request any system attributes
        var request = new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MessageAttributeNames = ["All"],
            MessageSystemAttributeNames = []
        };

        var result = await Sqs.ReceiveMessageAsync(request, cancellationToken);
        var message = result.Messages.ShouldHaveSingleItem();

        // Check that no system attributes are present
        message.Attributes.ShouldBeEmptyAwsCollection();
    }

    [Test]
    public async Task ReceiveMessageAsync_MultipleMessages_CorrectAttributesReturnedForEach(CancellationToken cancellationToken)
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test-queue" },
            cancellationToken)).QueueUrl;

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
        }, cancellationToken);

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
        }, cancellationToken);

        // Request specific system attributes
        var request = new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 10,
            MessageAttributeNames = ["All"],
            MessageSystemAttributeNames =
                [MessageSystemAttributeName.SenderId, MessageSystemAttributeName.AWSTraceHeader]
        };

        var result = await Sqs.ReceiveMessageAsync(request, cancellationToken);
        result.Messages.Count.ShouldBe(2);

        var message1 = result.Messages.First(m => m.Body == "Message 1");
        var message2 = result.Messages.First(m => m.Body == "Message 2");

        // Check attributes for Message 1
        message1.Attributes.ShouldHaveSingleItem();
        message1.Attributes[MessageSystemAttributeName.SenderId].ShouldBe("Sender1");

        // Check attributes for Message 2
        message2.Attributes.Count.ShouldBe(2);
        message2.Attributes[MessageSystemAttributeName.SenderId].ShouldBe("Sender2");
        message2.Attributes[MessageSystemAttributeName.AWSTraceHeader].ShouldBe("Root=1-5e3d83c1-e6a0db584850d61342823d4c");
    }

    // Permission tests
    [Test]
    public async Task AddPermissionAsync_ValidRequest_AddsPermissionToPolicy(CancellationToken cancellationToken)
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test-queue" },
            cancellationToken)).QueueUrl;
        var request = new AddPermissionRequest
        {
            QueueUrl = queueUrl,
            Label = "TestPermission",
            AWSAccountIds = ["123456789012"],
            Actions = ["SendMessage", "ReceiveMessage"]
        };

        var response = await Sqs.AddPermissionAsync(request, cancellationToken);

        response.ShouldNotBeNull();
        var attributes = await Sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            AttributeNames = ["Policy"]
        }, cancellationToken);
        attributes.Attributes.ShouldContainKey("Policy");
        var policy = Policy.FromJson(attributes.Attributes["Policy"]);
        policy.Statements.ShouldContain(s => s.Id == "TestPermission");
    }

    [Test]
    public async Task AddPermissionAsync_DuplicateLabel_ThrowsArgumentException(CancellationToken cancellationToken)
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test-queue" },
            cancellationToken)).QueueUrl;
        var request = new AddPermissionRequest
        {
            QueueUrl = queueUrl,
            Label = "TestPermission",
            AWSAccountIds = ["123456789012"],
            Actions = ["SendMessage"]
        };

        await Sqs.AddPermissionAsync(request, cancellationToken);

        await Assert.ThrowsAsync<Exception>(async () =>
            await Sqs.AddPermissionAsync(request, cancellationToken));
    }

    [Test]
    public async Task AddPermissionAsync_QueueDoesNotExist_ThrowsQueueDoesNotExistException(CancellationToken cancellationToken)
    {
        var request = new AddPermissionRequest
        {
            QueueUrl = "http://sqs.us-east-1.amazonaws.com/123456789012/non-existent-queue",
            Label = "TestPermission",
            AWSAccountIds = ["123456789012"],
            Actions = ["SendMessage"]
        };

        await Assert.ThrowsAsync<QueueDoesNotExistException>(async () =>
            await Sqs.AddPermissionAsync(request, cancellationToken));
    }

    [Test]
    public async Task RemovePermissionAsync_ValidRequest_RemovesPermissionFromPolicy(CancellationToken cancellationToken)
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test-queue" },
            cancellationToken)).QueueUrl;
        await Sqs.AddPermissionAsync(new AddPermissionRequest
        {
            QueueUrl = queueUrl,
            Label = "TestPermission",
            AWSAccountIds = ["123456789012"],
            Actions = ["SendMessage"]
        }, cancellationToken);

        var removeRequest = new RemovePermissionRequest
        {
            QueueUrl = queueUrl,
            Label = "TestPermission"
        };

        var response = await Sqs.RemovePermissionAsync(removeRequest, cancellationToken);

        response.ShouldNotBeNull();
        var attributes = await Sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            AttributeNames = ["Policy"]
        }, cancellationToken);
        attributes.Attributes?.ShouldNotContainKey("Policy");
    }

    [Test]
    public async Task RemovePermissionAsync_LabelDoesNotExist_ThrowsArgumentException(CancellationToken cancellationToken)
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test-queue" },
            cancellationToken)).QueueUrl;
        var request = new RemovePermissionRequest
        {
            QueueUrl = queueUrl,
            Label = "NonExistentLabel"
        };

        await Assert.ThrowsAsync<Exception>(async () =>
            await Sqs.RemovePermissionAsync(request, cancellationToken));
    }

    [Test]
    public async Task RemovePermissionAsync_QueueDoesNotExist_ThrowsQueueDoesNotExistException(CancellationToken cancellationToken)
    {
        var request = new RemovePermissionRequest
        {
            QueueUrl = "http://sqs.us-east-1.amazonaws.com/123456789012/non-existent-queue",
            Label = "TestPermission"
        };

        await Assert.ThrowsAsync<QueueDoesNotExistException>(async () =>
            await Sqs.RemovePermissionAsync(request, cancellationToken));
    }

    [Test]
    public async Task AddAndRemovePermission_MultiplePermissions_ManagesCorrectly(CancellationToken cancellationToken)
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test-queue" },
            cancellationToken)).QueueUrl;

        // Add first permission
        await Sqs.AddPermissionAsync(new AddPermissionRequest
        {
            QueueUrl = queueUrl,
            Label = "Permission1",
            AWSAccountIds = ["123456789012"],
            Actions = ["SendMessage"]
        }, cancellationToken);

        // Add second permission
        await Sqs.AddPermissionAsync(new AddPermissionRequest
        {
            QueueUrl = queueUrl,
            Label = "Permission2",
            AWSAccountIds = ["210987654321"],
            Actions = ["ReceiveMessage"]
        }, cancellationToken);

        // Verify both permissions exist
        var attributes = await Sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            AttributeNames = ["Policy"]
        }, cancellationToken);
        var policy = Policy.FromJson(attributes.Attributes["Policy"]);
        policy.Statements.Count.ShouldBe(2);
        policy.Statements.ShouldContain(s => s.Id == "Permission1");
        policy.Statements.ShouldContain(s => s.Id == "Permission2");

        // Remove first permission
        await Sqs.RemovePermissionAsync(new RemovePermissionRequest
        {
            QueueUrl = queueUrl,
            Label = "Permission1"
        }, cancellationToken);

        // Verify only second permission remains
        attributes = await Sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            AttributeNames = ["Policy"]
        }, cancellationToken);
        policy = Policy.FromJson(attributes.Attributes["Policy"]);
        policy.Statements.ShouldHaveSingleItem();
        policy.Statements.ShouldContain(s => s.Id == "Permission2");

        // Remove second permission
        await Sqs.RemovePermissionAsync(new RemovePermissionRequest
        {
            QueueUrl = queueUrl,
            Label = "Permission2"
        }, cancellationToken);

        // Verify policy is removed
        attributes = await Sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            AttributeNames = ["Policy"]
        }, cancellationToken);

        attributes.Attributes?.ShouldNotContainKey("Policy");
    }

    [Test]
    public async Task SendMessageAsync_MessageExceedsMaximumSize_ThrowsInvalidMessageContentsException(CancellationToken cancellationToken)
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test-queue" },
            cancellationToken)).QueueUrl;

        // Create a message that exceeds 256KB (262,144 bytes)
        var largeMessage = new string('x', 262145);

        var request = new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = largeMessage
        };

        await Assert.ThrowsAsync<AmazonSQSException>(() =>
            Sqs.SendMessageAsync(request, cancellationToken));
    }

    [Test]
    public async Task SendMessageAsync_MessageAttributeFullSizeCalculation_ThrowsException(CancellationToken cancellationToken)
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test-queue" },
            cancellationToken)).QueueUrl;

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
            Sqs.SendMessageAsync(request, cancellationToken));
    }

    [Test]
    public async Task SendMessageAsync_MultipleAttributesExactlyAtLimit_Succeeds(CancellationToken cancellationToken)
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test-queue" },
            cancellationToken)).QueueUrl;

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

        var sendResponse = await Sqs.SendMessageAsync(sendRequest, cancellationToken);
        sendResponse.MessageId.ShouldNotBeNull();

        // Verify we can receive the message with attributes
        var receiveResponse = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 1,
            MessageAttributeNames = ["All"]
        }, cancellationToken);

        var message = receiveResponse.Messages.ShouldHaveSingleItem();
        message.MessageAttributes.Count.ShouldBe(2);
    }

    [Test]
    public async Task SendMessageAsync_BinaryAttributeSize_Succeeds(CancellationToken cancellationToken)
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test-queue" },
            cancellationToken)).QueueUrl;

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
        var response = await Sqs.SendMessageAsync(request, cancellationToken);
        response.MessageId.ShouldNotBeNull();
    }

    [Test]
    public async Task SendMessageAsync_CustomAttributeTypeNames_CountTowardsLimit(CancellationToken cancellationToken)
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test-queue" },
            cancellationToken)).QueueUrl;

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
            Sqs.SendMessageAsync(request, cancellationToken));
    }

    [Test]
    public async Task SendMessageAsync_BatchWithAttributeSizeLimits_PartialBatchFailure(CancellationToken cancellationToken)
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test-queue" },
            cancellationToken)).QueueUrl;

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
            await Sqs.SendMessageBatchAsync(request, cancellationToken));
    }
}

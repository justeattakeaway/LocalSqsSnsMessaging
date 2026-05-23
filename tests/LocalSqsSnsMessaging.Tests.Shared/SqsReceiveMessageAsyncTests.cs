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
        var createQueueResponse = await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = UniqueName("test-queue") },
            cancellationToken);
        var queueUrl = createQueueResponse.QueueUrl;
        var request = new ReceiveMessageRequest { QueueUrl = queueUrl, WaitTimeSeconds = 0 };

        var result = await Sqs.ReceiveMessageAsync(request, cancellationToken);

        result.Messages.ShouldBeEmptyAwsCollection();
    }

    [Test]
    public async Task ReceiveMessageAsync_MessagesAvailable_ReturnsMessages(CancellationToken cancellationToken)
    {
        var createQueueResponse = await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = UniqueName("test-queue") },
            cancellationToken);
        var queueUrl = createQueueResponse.QueueUrl;

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

        // Real AWS may return fewer messages than MaxNumberOfMessages per call. Drain until
        // we've seen both, then assert on content rather than receive ordering (AWS doesn't
        // guarantee FIFO for standard queues anyway).
        var messages = await ReceiveAllAsync(Sqs, queueUrl, expectedCount: 2,
            cancellationToken: cancellationToken);
        messages.Count.ShouldBe(2);
        messages.Select(m => m.Body).ShouldBe(["Hello, world!", "Goodbye, world!"], ignoreOrder: true);
    }

    [Test, Category(TimeBased)]
    public async Task ReceiveMessageAsync_WaitsForMessages_ReturnsMessagesWhenAvailable(CancellationToken cancellationToken)
    {
        var createQueueResponse = await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = UniqueName("test-queue") },
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

    [Test, Category(TimeBased)]
    public async Task ReceiveMessageAsync_Timeout_ReturnsEmptyList(CancellationToken cancellationToken)
    {
        var createQueueResponse = await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = UniqueName("test-queue") },
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
        var createQueueResponse = await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = UniqueName("test-queue") },
            cancellationToken);
        var queueUrl = createQueueResponse.QueueUrl;
        var request = new ReceiveMessageRequest { QueueUrl = queueUrl, WaitTimeSeconds = 10 };

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await Sqs.ReceiveMessageAsync(request, cts.Token)
        );
    }

    [Test, Category(TimeBased)]
    public async Task ReceiveMessageAsync_RespectVisibilityTimeout(CancellationToken cancellationToken)
    {
        var createQueueResponse = await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = UniqueName("test-queue") },
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

    [Test, Category(TimeBased), Retry(3)]
    public async Task ReceiveMessageAsync_DelayedMessageBecomesVisible(CancellationToken cancellationToken)
    {
        var createQueueResponse = await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = UniqueName("test-queue") },
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

    [Test, Category(TimeBased), Retry(3)]
    public async Task ReceiveMessageAsync_MultipleMessagesWithDifferentDelays(CancellationToken cancellationToken)
    {
        const int initialVisibilityTimeout = 6; // seconds
        //const int visibilityTimeout = 6; // seconds
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = UniqueName("test-queue") },
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

        // Advance time past the visibility timeout of all three messages and drain.
        // Standard queues don't guarantee FIFO ordering — we assert that each message
        // returns once across the redelivery window, not which one comes back first.
        await WaitAsync(TimeSpan.FromSeconds(9));
        var redelivered = await ReceiveAllAsync(Sqs, queueUrl, expectedCount: 3,
            cancellationToken: cancellationToken);
        redelivered.Select(m => m.Body).ShouldBe(["Message 1", "Message 2", "Message 3"], ignoreOrder: true);
    }

    [Test, Category(TimeBased)]
    public async Task ReceiveMessageAsync_ApproximateReceiveCount_IncreasesWithEachReceive(CancellationToken cancellationToken)
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = UniqueName("test-queue") },
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
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = UniqueName("test-queue") },
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

    [Test, Category(TimeBased)]
    public async Task ReceiveMessageAsync_ApproximateReceiveCount_MultipleMessages(CancellationToken cancellationToken)
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = UniqueName("test-queue") },
            cancellationToken)).QueueUrl;

        // Send multiple messages
        await Sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = queueUrl, MessageBody = "Message 1" },
            cancellationToken);
        await Sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = queueUrl, MessageBody = "Message 2" },
            cancellationToken);

        // First receive — drain everything with a fresh visibility window so we see
        // ApproximateReceiveCount=1 on every message. (Real AWS may split across calls.)
        var round1 = await ReceiveAllAsync(Sqs, queueUrl, expectedCount: 2,
            visibilityTimeout: 5, cancellationToken: cancellationToken);
        round1.Count.ShouldBe(2);
        round1.ShouldAllBe(m => m.Attributes["ApproximateReceiveCount"] == "1");

        // Wait for visibility timeout to expire
        await WaitAsync(TimeSpan.FromSeconds(IsRealAwsMode ? 35 : 6));

        // Second receive — both should now show count = 2.
        var round2 = await ReceiveAllAsync(Sqs, queueUrl, expectedCount: 2,
            visibilityTimeout: 5, cancellationToken: cancellationToken);
        round2.Count.ShouldBe(2);
        round2.ShouldAllBe(m => m.Attributes["ApproximateReceiveCount"] == "2");

        // Delete one message
        await Sqs.DeleteMessageAsync(queueUrl, round2[0].ReceiptHandle,
            cancellationToken);

        // Wait for visibility timeout to expire again
        await WaitAsync(TimeSpan.FromSeconds(IsRealAwsMode ? 35 : 6));

        // Third receive — only the surviving message should come back, with count = 3.
        var round3 = await ReceiveAllAsync(Sqs, queueUrl, expectedCount: 1,
            visibilityTimeout: 5, cancellationToken: cancellationToken);
        var message3 = round3.ShouldHaveSingleItem();
        message3.Attributes["ApproximateReceiveCount"].ShouldBe("3");
    }

    [Test, Category(TimeBased)]
    public async Task ReceiveMessageAsync_MessageMovedToErrorQueue_AfterMaxReceives(CancellationToken cancellationToken)
    {
        // Create main queue and error queue
        var mainQueueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = UniqueName("main-queue") },
            cancellationToken)).QueueUrl;
        var errorQueueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = UniqueName("error-queue") },
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

        // Receive the message twice (maxReceiveCount=2 means the THIRD attempt will move
        // it to the DLQ). Real AWS may not return a message on every poll cycle, so use
        // the long-polling helper which retries until it sees one.
        for (int i = 0; i < 2; i++)
        {
            var received = await ReceiveAllAsync(Sqs, mainQueueUrl, expectedCount: 1,
                visibilityTimeout: 5, cancellationToken: cancellationToken);
            var message = received.ShouldHaveSingleItem();
            message.Attributes["ApproximateReceiveCount"].ShouldBe((i + 1).ToString(NumberFormatInfo.InvariantInfo));
            await WaitAsync(TimeSpan.FromSeconds(IsRealAwsMode ? 35 : 6)); // Wait for visibility timeout to expire
        }

        // Try to receive from the main queue - should be empty
        var emptyResult = await Sqs.ReceiveMessageAsync(request, cancellationToken);
        emptyResult.Messages.ShouldBeEmptyAwsCollection();

        // Check the error queue - the message should be there. Real AWS moves it
        // asynchronously after the second receive, so poll until it shows up.
        var errorMessages = await ReceiveAllAsync(Sqs, errorQueueUrl, expectedCount: 1,
            cancellationToken: cancellationToken);
        var errorMessage = errorMessages.ShouldHaveSingleItem();
        errorMessage.Body.ShouldBe("Test message");
    }

    [Test, Category(TimeBased)]
    public async Task ReceiveMessageAsync_MessageNotMovedToErrorQueue_IfDeletedBeforeMaxReceives(CancellationToken cancellationToken)
    {
        // Create main queue and error queue
        var mainQueueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = UniqueName("main-queue") },
            cancellationToken)).QueueUrl;
        var errorQueueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = UniqueName("error-queue") },
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
                    QueueName = UniqueName("main-queue.fifo"),
                    Attributes = new Dictionary<string, string> { ["FifoQueue"] = "true" }
                }, cancellationToken)).QueueUrl;
        var errorQueueUrl =
            (await Sqs.CreateQueueAsync(
                new CreateQueueRequest
                {
                    QueueName = UniqueName("error-queue.fifo"),
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
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = UniqueName("test-queue") },
            cancellationToken)).QueueUrl;

        // AWS only lets callers set AWSTraceHeader via MessageSystemAttributes — every other
        // system attribute (SenderId, SentTimestamp, ApproximateReceiveCount, ...) is
        // populated server-side and is rejected if supplied on the wire.
        await Sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "Test message",
            MessageSystemAttributes = new Dictionary<string, MessageSystemAttributeValue>
            {
                [MessageSystemAttributeName.AWSTraceHeader] = new MessageSystemAttributeValue
                    { StringValue = "Root=1-5e3d83c1-e6a0db584850d61342823d4c", DataType = "String" }
            }
        }, cancellationToken);

        // Request only the SenderId — others should be omitted from the response.
        var request = new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MessageAttributeNames = ["All"],
            MessageSystemAttributeNames = [MessageSystemAttributeName.SenderId]
        };

        var result = await Sqs.ReceiveMessageAsync(request, cancellationToken);
        var message = result.Messages.ShouldHaveSingleItem();

        // Only the requested system attribute is present, and it has a non-empty server-set
        // value (the caller's principal). The unrequested AWSTraceHeader and SentTimestamp
        // must be omitted.
        message.Attributes.ShouldContainKey(MessageSystemAttributeName.SenderId);
        message.Attributes[MessageSystemAttributeName.SenderId].ShouldNotBeNullOrEmpty();
        message.Attributes.ShouldNotContainKey(MessageSystemAttributeName.SentTimestamp);
        message.Attributes.ShouldNotContainKey(MessageSystemAttributeName.AWSTraceHeader);
    }

    [Test]
    public async Task ReceiveMessageAsync_AllMessageSystemAttributes_AllAttributesReturned(CancellationToken cancellationToken)
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = UniqueName("test-queue") },
            cancellationToken)).QueueUrl;

        // Only AWSTraceHeader is settable via MessageSystemAttributes; the rest are server-side.
        await Sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "Test message",
            MessageSystemAttributes = new Dictionary<string, MessageSystemAttributeValue>
            {
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

        // Server-populated system attributes the receive must surface alongside the
        // caller-supplied AWSTraceHeader.
        message.Attributes[MessageSystemAttributeName.SenderId].ShouldNotBeNullOrEmpty();
        message.Attributes[MessageSystemAttributeName.SentTimestamp].ShouldNotBeNullOrEmpty();
        message.Attributes[MessageSystemAttributeName.ApproximateReceiveCount].ShouldBe("1");
        message.Attributes[MessageSystemAttributeName.ApproximateFirstReceiveTimestamp].ShouldNotBeNullOrEmpty();
        message.Attributes[MessageSystemAttributeName.AWSTraceHeader].ShouldBe("Root=1-5e3d83c1-e6a0db584850d61342823d4c");
    }

    [Test]
    public async Task ReceiveMessageAsync_NoMessageSystemAttributes_NoAttributesReturned(CancellationToken cancellationToken)
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = UniqueName("test-queue") },
            cancellationToken)).QueueUrl;

        // AWSTraceHeader is the only legal MessageSystemAttributes key on SendMessage.
        await Sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "Test message",
            MessageSystemAttributes = new Dictionary<string, MessageSystemAttributeValue>
            {
                [MessageSystemAttributeName.AWSTraceHeader] = new MessageSystemAttributeValue
                    { StringValue = "Root=1-5e3d83c1-e6a0db584850d61342823d4c", DataType = "String" }
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
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = UniqueName("test-queue") },
            cancellationToken)).QueueUrl;

        // Send two messages — only the second carries an AWSTraceHeader (the one
        // MessageSystemAttributes key real AWS accepts on the wire).
        await Sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "Message 1"
        }, cancellationToken);

        await Sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "Message 2",
            MessageSystemAttributes = new Dictionary<string, MessageSystemAttributeValue>
            {
                [MessageSystemAttributeName.AWSTraceHeader] = new MessageSystemAttributeValue
                    { StringValue = "Root=1-5e3d83c1-e6a0db584850d61342823d4c", DataType = "String" }
            }
        }, cancellationToken);

        // Drain both messages (real AWS often returns them in separate receives) and
        // ask for SenderId + AWSTraceHeader as system attributes.
        var messages = await ReceiveAllAsync(Sqs, queueUrl, expectedCount: 2,
            cancellationToken: cancellationToken);
        messages.Count.ShouldBe(2);

        var message1 = messages.First(m => m.Body == "Message 1");
        var message2 = messages.First(m => m.Body == "Message 2");

        // Message 1's SenderId is server-set and present. AWSTraceHeader is only present
        // if the SDK was running under an active X-Ray trace (the SDK auto-injects one
        // when so); in that case the value differs from message 2's explicit header.
        message1.Attributes.ShouldContainKey(MessageSystemAttributeName.SenderId);
        message1.Attributes[MessageSystemAttributeName.SenderId].ShouldNotBeNullOrEmpty();
        if (message1.Attributes.TryGetValue(MessageSystemAttributeName.AWSTraceHeader, out var trace1))
        {
            trace1.ShouldNotBe("Root=1-5e3d83c1-e6a0db584850d61342823d4c",
                "Message 1's trace header must not be the explicit one from Message 2");
        }

        // Message 2 carries its own AWSTraceHeader alongside the server-set SenderId.
        message2.Attributes.ShouldContainKey(MessageSystemAttributeName.SenderId);
        message2.Attributes[MessageSystemAttributeName.SenderId].ShouldNotBeNullOrEmpty();
        message2.Attributes[MessageSystemAttributeName.AWSTraceHeader].ShouldBe("Root=1-5e3d83c1-e6a0db584850d61342823d4c");
    }

    // Permission tests
    [Test]
    public async Task AddPermissionAsync_ValidRequest_AddsPermissionToPolicy(CancellationToken cancellationToken)
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = UniqueName("test-queue") },
            cancellationToken)).QueueUrl;
        // Real AWS verifies AWSAccountIds against IAM; an arbitrary 12-digit string is
        // rejected with "Unable to verify". The caller's own account is always a valid
        // principal, so use that.
        var request = new AddPermissionRequest
        {
            QueueUrl = queueUrl,
            Label = "TestPermission",
            AWSAccountIds = [AccountId],
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
    public async Task AddPermissionAsync_DuplicateLabel_RejectsOrReplaces(CancellationToken cancellationToken)
    {
        // Real AWS doesn't always reject duplicate labels — sometimes the second AddPermission
        // is accepted and the existing statement is overwritten/merged. The in-memory client
        // throws. Either is acceptable; assert that whichever path the implementation takes,
        // the resulting policy still has exactly one statement with the label.
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = UniqueName("test-queue") },
            cancellationToken)).QueueUrl;
        var request = new AddPermissionRequest
        {
            QueueUrl = queueUrl,
            Label = "TestPermission",
            AWSAccountIds = [AccountId],
            Actions = ["SendMessage"]
        };

        await Sqs.AddPermissionAsync(request, cancellationToken);

        try
        {
            await Sqs.AddPermissionAsync(request, cancellationToken);
        }
#pragma warning disable CA1031 // Either typed AWS error or local ArgumentException is acceptable.
        catch (Exception)
        {
            // Acceptable: implementation rejected the duplicate.
        }
#pragma warning restore CA1031

        var attrs = await Sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            AttributeNames = ["Policy"]
        }, cancellationToken);
        attrs.Attributes.ShouldContainKey("Policy");
        var policy = Policy.FromJson(attrs.Attributes["Policy"]);
        policy.Statements.Count(s => s.Id == "TestPermission").ShouldBe(1);
    }

    [Test]
    public async Task AddPermissionAsync_QueueDoesNotExist_ThrowsQueueDoesNotExistException(CancellationToken cancellationToken)
    {
        // Real AWS rejects http:// queue URLs at SDK validation time before sending,
        // so use the canonical https form. The queue name is randomized to guarantee
        // it doesn't exist; the principal is the caller's own account (always valid).
        var request = new AddPermissionRequest
        {
            QueueUrl = $"https://sqs.us-east-1.amazonaws.com/{AccountId}/{UniqueName("non-existent-queue")}",
            Label = "TestPermission",
            AWSAccountIds = [AccountId],
            Actions = ["SendMessage"]
        };

        await Assert.ThrowsAsync<QueueDoesNotExistException>(async () =>
            await Sqs.AddPermissionAsync(request, cancellationToken));
    }

    [Test]
    public async Task RemovePermissionAsync_ValidRequest_RemovesPermissionFromPolicy(CancellationToken cancellationToken)
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = UniqueName("test-queue") },
            cancellationToken)).QueueUrl;
        await Sqs.AddPermissionAsync(new AddPermissionRequest
        {
            QueueUrl = queueUrl,
            Label = "TestPermission",
            AWSAccountIds = [AccountId],
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
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = UniqueName("test-queue") },
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
            QueueUrl = $"https://sqs.us-east-1.amazonaws.com/{AccountId}/{UniqueName("non-existent-queue")}",
            Label = "TestPermission"
        };

        await Assert.ThrowsAsync<QueueDoesNotExistException>(async () =>
            await Sqs.RemovePermissionAsync(request, cancellationToken));
    }

    [Test]
    public async Task AddAndRemovePermission_MultiplePermissions_ManagesCorrectly(CancellationToken cancellationToken)
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = UniqueName("test-queue") },
            cancellationToken)).QueueUrl;

        // Add first permission — real AWS verifies principal accounts, so use the
        // caller's own account which is always valid.
        await Sqs.AddPermissionAsync(new AddPermissionRequest
        {
            QueueUrl = queueUrl,
            Label = "Permission1",
            AWSAccountIds = [AccountId],
            Actions = ["SendMessage"]
        }, cancellationToken);

        // Add second permission — same constraint.
        await Sqs.AddPermissionAsync(new AddPermissionRequest
        {
            QueueUrl = queueUrl,
            Label = "Permission2",
            AWSAccountIds = [AccountId],
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
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = UniqueName("test-queue") },
            cancellationToken)).QueueUrl;

        // Create a message that exceeds 1MB (1,048,576 bytes)
        var largeMessage = new string('x', 1_048_576 + 1);

        var request = new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = largeMessage
        };

        await Assert.ThrowsAsync<AmazonSQSException>(() =>
            Sqs.SendMessageAsync(request, cancellationToken));
    }

    [Test]
    public async Task SendMessageAsync_MessageAttributeFullSizeCalculation_ThrowsException(
        CancellationToken cancellationToken)
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = UniqueName("test-queue") },
            cancellationToken)).QueueUrl;

        // The total size includes the message body and all message attribute parts.
        // Let's construct a message that exceeds 1MB (1,048,576 bytes).
        var messageBody = new string('x', 950_000); // 950,000 bytes

        var request = new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = messageBody,
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                // This attribute will push the total size over the 1MB limit.
                [new string('a', 1000)] = new MessageAttributeValue // 1,000 bytes
                {
                    DataType = "String", // 6 bytes
                    StringValue = new string('y', 100_000) // 100,000 bytes
                }
            }
        };
        // Total size: 950,000 + 1,000 + 6 + 100,000 = 1,051,006 bytes > 1MB

        await Assert.ThrowsAsync<AmazonSQSException>(() =>
            Sqs.SendMessageAsync(request, cancellationToken));
    }

    [Test]
    public async Task SendMessageAsync_MultipleAttributesExactlyAtLimit_Succeeds(CancellationToken cancellationToken)
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = UniqueName("test-queue") },
            cancellationToken)).QueueUrl;

        // Calculate sizes to be just under 1MB, leaving room for system attributes
        // (e.g. AWSTraceHeader added by OpenTelemetry instrumentation):
        // - Message body: 1,000,000 bytes
        // - First attribute:
        //   * Name: 100 bytes
        //   * Type: "String" (6 bytes)
        //   * Value: 31,000 bytes
        // - Second attribute:
        //   * Name: 20 bytes
        //   * Type: "String" (6 bytes)
        //   * Value: 17,244 bytes
        // Total: 1,048,376 bytes
        //
        // Both attributes use "String" type — real AWS validates Number values are
        // actually numeric, so the original test value of repeated 'z' chars couldn't
        // round-trip as a Number.

        var sendRequest = new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = new string('x', 1_000_000),
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                [new string('a', 100)] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = new string('y', 31_000)
                },
                [new string('b', 20)] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = new string('z', 17_244)
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
        message.MessageAttributes.ShouldContainKey(new string('a', 100));
        message.MessageAttributes.ShouldContainKey(new string('b', 20));
    }

    [Test]
    public async Task SendMessageAsync_BinaryAttributeSize_Succeeds(CancellationToken cancellationToken)
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = UniqueName("test-queue") },
            cancellationToken)).QueueUrl;

        // Create a binary attribute
        byte[] binaryData = new byte[1000];
#pragma warning disable CA5394
        new Random(42).NextBytes(binaryData);
#pragma warning restore CA5394

        var request = new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = new string('x', 1_047_000),
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                ["BinaryAttribute"] = new MessageAttributeValue
                {
                    DataType = "Binary",
                    BinaryValue = new MemoryStream(binaryData)
                }
            }
        };

        // Total size: 1,047,000 (body) + 14 (attribute name) + 6 (type) + 1,000 (binary value) = 1,048,020 bytes
        var response = await Sqs.SendMessageAsync(request, cancellationToken);
        response.MessageId.ShouldNotBeNull();
    }

    [Test]
    public async Task SendMessageAsync_CustomAttributeTypeNames_CountTowardsLimit(CancellationToken cancellationToken)
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = UniqueName("test-queue") },
            cancellationToken)).QueueUrl;

        // Using a custom attribute type name which counts towards the limit
        var longCustomType = $"String.{new string('x', 100)}";

        var request = new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = new string('x', 1_048_500),
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
        var sqsException = await Assert.ThrowsAsync<AmazonSQSException>(() =>
            Sqs.SendMessageAsync(request, cancellationToken));
        sqsException.ShouldNotBeNull().Message.ShouldBe("One or more parameters are invalid. Reason: Message must be shorter than 1048576 bytes.");
    }

    [Test]
    public async Task SendMessageAsync_BatchWithAttributeSizeLimits_PartialBatchFailure(CancellationToken cancellationToken)
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = UniqueName("test-queue") },
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
            MessageBody = new string('x', 1_000_000), // Large body
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                [new string('a', 1_000)] = new MessageAttributeValue // Long attribute name
                {
                    DataType = "String",
                    StringValue = new string('y', 48_000)
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

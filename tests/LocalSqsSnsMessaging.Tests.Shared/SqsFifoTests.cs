using Amazon.SQS;
using Amazon.SQS.Model;
using Shouldly;

namespace LocalSqsSnsMessaging.Tests;

public abstract class SqsFifoTests : WaitingTestBase
{
    protected IAmazonSQS Sqs = null!;
    protected string AccountId = null!;

    [Test]
    public async Task CreateFifoQueue_SetsCorrectAttributes(CancellationToken cancellationToken)
    {
        var queueName = UniqueName("test-fifo-queue.fifo");
        var createQueueResponse = await Sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = queueName,
            Attributes = new Dictionary<string, string>
            {
                [QueueAttributeName.FifoQueue] = "true"
            }
        }, cancellationToken);

        var attributes = await Sqs.GetQueueAttributesAsync(createQueueResponse.QueueUrl, [QueueAttributeName.FifoQueue], cancellationToken);

        bool.Parse(attributes.Attributes[QueueAttributeName.FifoQueue]).ShouldBeTrue();
    }

    [Test]
    public async Task SendMessageToFifoQueue_RequiresMessageGroupId(CancellationToken cancellationToken)
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = UniqueName("test-fifo-queue.fifo"),
            Attributes = new Dictionary<string, string> { [QueueAttributeName.FifoQueue] = "true" }
        }, cancellationToken)).QueueUrl;

        await Assert.ThrowsAsync<Exception>(async () =>
            await Sqs.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = queueUrl,
                MessageBody = "Test Message"
                // MessageGroupId is missing
            }, cancellationToken));
    }

    [Test]
    public async Task FifoQueue_EnforcesMessageGroupOrdering(CancellationToken cancellationToken)
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = UniqueName("test-fifo-queue.fifo"),
            Attributes = new Dictionary<string, string> { [QueueAttributeName.FifoQueue] = "true" }
        }, cancellationToken)).QueueUrl;

        // Send messages to two different message groups
        await Sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "Message 1 Group A",
            MessageGroupId = "GroupA",
            MessageDeduplicationId = "Dedup1A"
        }, cancellationToken);

        await Sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "Message 2 Group A",
            MessageGroupId = "GroupA",
            MessageDeduplicationId = "Dedup2A"
        }, cancellationToken);

        await Sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "Message 1 Group B",
            MessageGroupId = "GroupB",
            MessageDeduplicationId = "Dedup1B"
        }, cancellationToken);

        // Receive all messages (may require multiple polls against real SQS/moto)
        var messages = await ReceiveAllMessagesAsync(Sqs, queueUrl, 3, cancellationToken,
            ["MessageGroupId"]);

        messages.Count.ShouldBe(3);

        // Verify within-group ordering (cross-group order is not guaranteed by SQS)
        var groupAMessages = messages.Where(m => m.Attributes["MessageGroupId"] == "GroupA").ToList();
        groupAMessages.Count.ShouldBe(2);
        groupAMessages[0].Body.ShouldBe("Message 1 Group A");
        groupAMessages[1].Body.ShouldBe("Message 2 Group A");

        var groupBMessages = messages.Where(m => m.Attributes["MessageGroupId"] == "GroupB").ToList();
        groupBMessages.Count.ShouldBe(1);
        groupBMessages[0].Body.ShouldBe("Message 1 Group B");
    }

    [Test]
    public async Task FifoQueue_MessageDeduplication(CancellationToken cancellationToken)
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = UniqueName("test-fifo-queue.fifo"),
            Attributes = new Dictionary<string, string>
            {
                [QueueAttributeName.FifoQueue] = "true",
                [QueueAttributeName.ContentBasedDeduplication] = "false"
            }
        }, cancellationToken)).QueueUrl;

        // Send two messages with the same deduplication ID
        await Sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "Duplicate Message",
            MessageGroupId = "GroupA",
            MessageDeduplicationId = "DuplicateDedup"
        }, cancellationToken);

        await Sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "Duplicate Message",
            MessageGroupId = "GroupA",
            MessageDeduplicationId = "DuplicateDedup"
        }, cancellationToken);

        // Send a message with a different deduplication ID
        await Sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "Unique Message",
            MessageGroupId = "GroupA",
            MessageDeduplicationId = "UniqueDedup"
        }, cancellationToken);

        // Receive all messages (may require multiple polls against real SQS/moto)
        var messages = await ReceiveAllMessagesAsync(Sqs, queueUrl, 2, cancellationToken);

        messages.Count.ShouldBe(2);
        messages[0].Body.ShouldBe("Duplicate Message");
        messages[1].Body.ShouldBe("Unique Message");
    }

    [Test]
    public async Task FifoQueue_ContentBasedDeduplication(CancellationToken cancellationToken)
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = UniqueName("test-content-dedup-queue.fifo"),
            Attributes = new Dictionary<string, string>
            {
                [QueueAttributeName.FifoQueue] = "true",
                [QueueAttributeName.ContentBasedDeduplication] = "true"
            }
        }, cancellationToken)).QueueUrl;

        // Send two identical messages without specifying MessageDeduplicationId
        await Sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "Duplicate Content",
            MessageGroupId = "GroupA"
        }, cancellationToken);

        await Sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "Duplicate Content",
            MessageGroupId = "GroupA"
        }, cancellationToken);

        // Send a different message
        await Sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "Unique Content",
            MessageGroupId = "GroupA"
        }, cancellationToken);

        // Receive all messages (may require multiple polls against real SQS/moto)
        var messages = await ReceiveAllMessagesAsync(Sqs, queueUrl, 2, cancellationToken);

        messages.Count.ShouldBe(2);
        messages[0].Body.ShouldBe("Duplicate Content");
        messages[1].Body.ShouldBe("Unique Content");
    }

    [Test]
    public async Task SendMessageBatchToFifoQueue_MessagesAreReceivable(CancellationToken cancellationToken)
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = UniqueName("test-fifo-batch-queue.fifo"),
            Attributes = new Dictionary<string, string>
            {
                [QueueAttributeName.FifoQueue] = "true",
                [QueueAttributeName.ContentBasedDeduplication] = "true"
            }
        }, cancellationToken)).QueueUrl;

        var sendResponse = await Sqs.SendMessageBatchAsync(new SendMessageBatchRequest
        {
            QueueUrl = queueUrl,
            Entries =
            [
                new SendMessageBatchRequestEntry { Id = "1", MessageBody = "a", MessageGroupId = "g" },
                new SendMessageBatchRequestEntry { Id = "2", MessageBody = "b", MessageGroupId = "g" }
            ]
        }, cancellationToken);

        sendResponse.Successful.Count.ShouldBe(2);
        (sendResponse.Failed?.Count ?? 0).ShouldBe(0);

        var messages = await ReceiveAllMessagesAsync(Sqs, queueUrl, 2, cancellationToken,
            ["MessageGroupId"]);

        messages.Count.ShouldBe(2);
        messages[0].Body.ShouldBe("a");
        messages[1].Body.ShouldBe("b");
        messages.ShouldAllBe(m => m.Attributes["MessageGroupId"] == "g");
    }

    [Test]
    public async Task SendMessageBatchToFifoQueue_RequiresMessageGroupId(CancellationToken cancellationToken)
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = UniqueName("test-fifo-batch-queue.fifo"),
            Attributes = new Dictionary<string, string> { [QueueAttributeName.FifoQueue] = "true" }
        }, cancellationToken)).QueueUrl;

        // An entry missing MessageGroupId is a per-entry failure, not a whole-batch exception.
        var sendResponse = await Sqs.SendMessageBatchAsync(new SendMessageBatchRequest
        {
            QueueUrl = queueUrl,
            Entries =
            [
                new SendMessageBatchRequestEntry { Id = "1", MessageBody = "a" }
                // MessageGroupId is missing
            ]
        }, cancellationToken);

        (sendResponse.Successful?.Count ?? 0).ShouldBe(0);
        sendResponse.Failed.Count.ShouldBe(1);
        sendResponse.Failed[0].Id.ShouldBe("1");
        sendResponse.Failed[0].SenderFault.ShouldBe(true);
        sendResponse.Failed[0].Code.ShouldBe("MissingParameter");
    }

    [Test]
    public async Task SendMessageBatchToFifoQueue_InvalidEntryDoesNotAbortValidEntries(CancellationToken cancellationToken)
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = UniqueName("test-fifo-batch-mixed-queue.fifo"),
            Attributes = new Dictionary<string, string>
            {
                [QueueAttributeName.FifoQueue] = "true",
                [QueueAttributeName.ContentBasedDeduplication] = "true"
            }
        }, cancellationToken)).QueueUrl;

        // Real SQS validates entries independently: the invalid entry lands in Failed
        // while the valid ones are enqueued and reported in Successful.
        var sendResponse = await Sqs.SendMessageBatchAsync(new SendMessageBatchRequest
        {
            QueueUrl = queueUrl,
            Entries =
            [
                new SendMessageBatchRequestEntry { Id = "1", MessageBody = "a", MessageGroupId = "g" },
                new SendMessageBatchRequestEntry { Id = "2", MessageBody = "b" }, // MessageGroupId is missing
                new SendMessageBatchRequestEntry { Id = "3", MessageBody = "c", MessageGroupId = "g" }
            ]
        }, cancellationToken);

        sendResponse.Successful.Count.ShouldBe(2);
        sendResponse.Successful.Select(e => e.Id).ShouldBe(["1", "3"], ignoreOrder: true);
        sendResponse.Failed.Count.ShouldBe(1);
        sendResponse.Failed[0].Id.ShouldBe("2");
        sendResponse.Failed[0].SenderFault.ShouldBe(true);

        var messages = await ReceiveAllMessagesAsync(Sqs, queueUrl, 2, cancellationToken);

        messages.Select(m => m.Body).ShouldBe(["a", "c"]);
    }

    [Test]
    public async Task FifoQueue_InFlightMessageBlocksMessageGroup(CancellationToken cancellationToken)
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = UniqueName("test-fifo-inflight-queue.fifo"),
            Attributes = new Dictionary<string, string>
            {
                [QueueAttributeName.FifoQueue] = "true",
                [QueueAttributeName.ContentBasedDeduplication] = "true"
            }
        }, cancellationToken)).QueueUrl;

        foreach (var body in new[] { "m1", "m2", "m3" })
        {
            await Sqs.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = queueUrl,
                MessageBody = body,
                MessageGroupId = "GroupA"
            }, cancellationToken);
        }

        var first = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 1,
            WaitTimeSeconds = IsRealAwsMode ? 5 : 0
        }, cancellationToken);

        first.Messages.ShouldHaveSingleItem().Body.ShouldBe("m1");

        // m1 is in flight (received but not deleted), so the whole group is locked:
        // no further messages from it until m1 is deleted or its visibility expires.
        var whileBlocked = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 1,
            WaitTimeSeconds = 0
        }, cancellationToken);

        (whileBlocked.Messages?.Count ?? 0).ShouldBe(0);

        // Deleting m1 unlocks the group and m2 becomes deliverable.
        await Sqs.DeleteMessageAsync(queueUrl, first.Messages[0].ReceiptHandle, cancellationToken);

        var afterDelete = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 1,
            WaitTimeSeconds = IsRealAwsMode ? 5 : 0
        }, cancellationToken);

        afterDelete.Messages.ShouldHaveSingleItem().Body.ShouldBe("m2");
    }

    [Test]
    public async Task FifoQueue_BlockedGroupDoesNotBlockOtherGroups(CancellationToken cancellationToken)
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = UniqueName("test-fifo-groups-queue.fifo"),
            Attributes = new Dictionary<string, string>
            {
                [QueueAttributeName.FifoQueue] = "true",
                [QueueAttributeName.ContentBasedDeduplication] = "true"
            }
        }, cancellationToken)).QueueUrl;

        await Sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "A1",
            MessageGroupId = "GroupA"
        }, cancellationToken);

        await Sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "B1",
            MessageGroupId = "GroupB"
        }, cancellationToken);

        // Receive one message at a time without deleting: each receive locks the
        // delivered message's group, but the other group must remain deliverable.
        var first = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 1,
            MessageSystemAttributeNames = ["MessageGroupId"],
            WaitTimeSeconds = IsRealAwsMode ? 5 : 0
        }, cancellationToken);

        var second = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 1,
            MessageSystemAttributeNames = ["MessageGroupId"],
            WaitTimeSeconds = IsRealAwsMode ? 5 : 0
        }, cancellationToken);

        var firstMessage = first.Messages.ShouldHaveSingleItem();
        var secondMessage = second.Messages.ShouldHaveSingleItem();

        firstMessage.Attributes["MessageGroupId"].ShouldNotBe(secondMessage.Attributes["MessageGroupId"]);
        new[] { firstMessage.Body, secondMessage.Body }.ShouldBe(["A1", "B1"], ignoreOrder: true);

        // With one message in flight per group, both groups are now locked.
        var whileBlocked = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 10,
            WaitTimeSeconds = 0
        }, cancellationToken);

        (whileBlocked.Messages?.Count ?? 0).ShouldBe(0);
    }

    [Test, Category(TimeBased)]
    public async Task FifoQueue_VisibilityTimeoutExpiry_RedeliversAtHeadOfGroup(CancellationToken cancellationToken)
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = UniqueName("test-fifo-redelivery-queue.fifo"),
            Attributes = new Dictionary<string, string>
            {
                [QueueAttributeName.FifoQueue] = "true",
                [QueueAttributeName.ContentBasedDeduplication] = "true"
            }
        }, cancellationToken)).QueueUrl;

        await Sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "m1",
            MessageGroupId = "GroupA"
        }, cancellationToken);

        await Sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "m2",
            MessageGroupId = "GroupA"
        }, cancellationToken);

        var first = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 1,
            VisibilityTimeout = 3,
            WaitTimeSeconds = IsRealAwsMode ? 5 : 0
        }, cancellationToken);

        var originalMessageId = first.Messages.ShouldHaveSingleItem().MessageId;
        first.Messages[0].Body.ShouldBe("m1");

        // Let m1's visibility timeout lapse without deleting it: it must become
        // visible again at the head of its group, ahead of m2, preserving order.
        await WaitAsync(TimeSpan.FromSeconds(4));

        var redelivered = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 10,
            WaitTimeSeconds = IsRealAwsMode ? 5 : 0
        }, cancellationToken);

        redelivered.Messages.Count.ShouldBe(2);
        redelivered.Messages[0].Body.ShouldBe("m1");
        redelivered.Messages[0].MessageId.ShouldBe(originalMessageId);
        redelivered.Messages[1].Body.ShouldBe("m2");
    }

    [Test, Category(TimeBased)]
    public async Task FifoQueue_ReceiveMessage_HonoursWaitTimeSeconds(CancellationToken cancellationToken)
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = UniqueName("test-fifo-longpoll-queue.fifo"),
            Attributes = new Dictionary<string, string>
            {
                [QueueAttributeName.FifoQueue] = "true",
                [QueueAttributeName.ContentBasedDeduplication] = "true"
            }
        }, cancellationToken)).QueueUrl;

        // An empty FIFO receive with WaitTimeSeconds must long-poll rather than
        // return immediately (which made poll loops hot-spin).
        var receiveTask = Sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            WaitTimeSeconds = 5
        }, cancellationToken);

        await WaitAsync(TimeSpan.FromSeconds(5));

        var result = await receiveTask;

        result.Messages.ShouldBeEmptyAwsCollection();
    }

    [Test, Category(TimeBased)]
    public async Task FifoQueue_ReceiveMessage_LongPollReturnsWhenMessageArrives(CancellationToken cancellationToken)
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = UniqueName("test-fifo-longpoll-arrival-queue.fifo"),
            Attributes = new Dictionary<string, string>
            {
                [QueueAttributeName.FifoQueue] = "true",
                [QueueAttributeName.ContentBasedDeduplication] = "true"
            }
        }, cancellationToken)).QueueUrl;

        var receiveTask = Sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            WaitTimeSeconds = 10
        }, cancellationToken);

        await WaitAsync(TimeSpan.FromSeconds(3));

        await Sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "Hello, world!",
            MessageGroupId = "GroupA"
        }, cancellationToken);

        var result = await receiveTask;

        result.Messages.ShouldHaveSingleItem().Body.ShouldBe("Hello, world!");
    }

    [Test]
    public async Task SendMessageBatchToFifoQueue_ContentBasedDeduplication(CancellationToken cancellationToken)
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = UniqueName("test-fifo-batch-dedup-queue.fifo"),
            Attributes = new Dictionary<string, string>
            {
                [QueueAttributeName.FifoQueue] = "true",
                [QueueAttributeName.ContentBasedDeduplication] = "true"
            }
        }, cancellationToken)).QueueUrl;

        // Two identical bodies (deduplicated) plus one unique body, in a single batch.
        var sendResponse = await Sqs.SendMessageBatchAsync(new SendMessageBatchRequest
        {
            QueueUrl = queueUrl,
            Entries =
            [
                new SendMessageBatchRequestEntry { Id = "1", MessageBody = "Duplicate Content", MessageGroupId = "g" },
                new SendMessageBatchRequestEntry { Id = "2", MessageBody = "Duplicate Content", MessageGroupId = "g" },
                new SendMessageBatchRequestEntry { Id = "3", MessageBody = "Unique Content", MessageGroupId = "g" }
            ]
        }, cancellationToken);

        // Every entry is still reported successful (deduplication is not a failure).
        sendResponse.Successful.Count.ShouldBe(3);

        var messages = await ReceiveAllMessagesAsync(Sqs, queueUrl, 2, cancellationToken);

        messages.Count.ShouldBe(2);
        messages[0].Body.ShouldBe("Duplicate Content");
        messages[1].Body.ShouldBe("Unique Content");
    }

    [Test]
    public async Task FifoQueue_HighThroughputMode(CancellationToken cancellationToken)
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = UniqueName("test-high-throughput-queue.fifo"),
            Attributes = new Dictionary<string, string>
            {
                [QueueAttributeName.FifoQueue] = "true",
                [QueueAttributeName.DeduplicationScope] = "messageGroup",
                [QueueAttributeName.FifoThroughputLimit] = "perMessageGroupId"
            }
        }, cancellationToken)).QueueUrl;

        // Send messages to different message groups
        for (int i = 0; i < 5; i++)
        {
            await Sqs.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = queueUrl,
                MessageBody = $"Message {i} Group A",
                MessageGroupId = "GroupA",
                MessageDeduplicationId = $"DedupA{i}"
            }, cancellationToken);

            await Sqs.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = queueUrl,
                MessageBody = $"Message {i} Group B",
                MessageGroupId = "GroupB",
                MessageDeduplicationId = $"DedupB{i}"
            }, cancellationToken);
        }

        // Receive all messages (may require multiple polls against real SQS/moto)
        var messages = await ReceiveAllMessagesAsync(Sqs, queueUrl, 10, cancellationToken);

        messages.Count.ShouldBe(10);
        // Verify that messages from both groups are present
        messages.ShouldContain(m => m.Body.Contains("Group A", StringComparison.Ordinal));
        messages.ShouldContain(m => m.Body.Contains("Group B", StringComparison.Ordinal));
    }
}

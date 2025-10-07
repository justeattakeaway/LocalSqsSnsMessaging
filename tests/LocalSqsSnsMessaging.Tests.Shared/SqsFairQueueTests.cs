using Amazon.SQS;
using Amazon.SQS.Model;
using Shouldly;

namespace LocalSqsSnsMessaging.Tests;

public abstract class SqsFairQueueTests : WaitingTestBase
{
    protected IAmazonSQS Sqs = null!;
    protected string AccountId = null!;

    [Test]
    public async Task CreateFairQueue_SetsCorrectAttributes(CancellationToken cancellationToken)
    {
        var queueName = "test-fair-queue.fifo";
        var createQueueResponse = await Sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = queueName,
            Attributes = new Dictionary<string, string>
            {
                [QueueAttributeName.FifoQueue] = "true",
                [QueueAttributeName.DeduplicationScope] = "messageGroup",
                [QueueAttributeName.FifoThroughputLimit] = "perMessageGroupId"
            }
        }, cancellationToken);

        var attributes = await Sqs.GetQueueAttributesAsync(
            createQueueResponse.QueueUrl,
            [QueueAttributeName.FifoQueue, QueueAttributeName.DeduplicationScope, QueueAttributeName.FifoThroughputLimit],
            cancellationToken);

        bool.Parse(attributes.Attributes[QueueAttributeName.FifoQueue]).ShouldBeTrue();
        attributes.Attributes[QueueAttributeName.DeduplicationScope].ShouldBe("messageGroup");
        attributes.Attributes[QueueAttributeName.FifoThroughputLimit].ShouldBe("perMessageGroupId");
    }

    [Test]
    public async Task FairQueue_AllowsMessagesFromMultipleGroups(CancellationToken cancellationToken)
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = "test-fair-queue.fifo",
            Attributes = new Dictionary<string, string>
            {
                [QueueAttributeName.FifoQueue] = "true",
                [QueueAttributeName.DeduplicationScope] = "messageGroup",
                [QueueAttributeName.FifoThroughputLimit] = "perMessageGroupId"
            }
        }, cancellationToken)).QueueUrl;

        // Send multiple messages to two different message groups
        // Group A: 3 messages
        await Sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "A1",
            MessageGroupId = "GroupA",
            MessageDeduplicationId = "DedupA1"
        }, cancellationToken);

        await Sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "A2",
            MessageGroupId = "GroupA",
            MessageDeduplicationId = "DedupA2"
        }, cancellationToken);

        await Sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "A3",
            MessageGroupId = "GroupA",
            MessageDeduplicationId = "DedupA3"
        }, cancellationToken);

        // Group B: 3 messages
        await Sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "B1",
            MessageGroupId = "GroupB",
            MessageDeduplicationId = "DedupB1"
        }, cancellationToken);

        await Sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "B2",
            MessageGroupId = "GroupB",
            MessageDeduplicationId = "DedupB2"
        }, cancellationToken);

        await Sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "B3",
            MessageGroupId = "GroupB",
            MessageDeduplicationId = "DedupB3"
        }, cancellationToken);

        // Receive messages
        var result = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 6,
            MessageSystemAttributeNames = ["MessageGroupId"]
        }, cancellationToken);

        result.Messages.Count.ShouldBe(6);

        // Verify all messages are received
        result.Messages.ShouldContain(m => m.Body == "A1");
        result.Messages.ShouldContain(m => m.Body == "A2");
        result.Messages.ShouldContain(m => m.Body == "A3");
        result.Messages.ShouldContain(m => m.Body == "B1");
        result.Messages.ShouldContain(m => m.Body == "B2");
        result.Messages.ShouldContain(m => m.Body == "B3");

        // Verify order within each group is preserved
        var groupAMessages = result.Messages.Where(m => m.Attributes["MessageGroupId"] == "GroupA").ToList();
        groupAMessages[0].Body.ShouldBe("A1");
        groupAMessages[1].Body.ShouldBe("A2");
        groupAMessages[2].Body.ShouldBe("A3");

        var groupBMessages = result.Messages.Where(m => m.Attributes["MessageGroupId"] == "GroupB").ToList();
        groupBMessages[0].Body.ShouldBe("B1");
        groupBMessages[1].Body.ShouldBe("B2");
        groupBMessages[2].Body.ShouldBe("B3");
    }

    [Test]
    public async Task FairQueue_HandlesUnevenMessageDistribution(CancellationToken cancellationToken)
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = "test-fair-uneven-queue.fifo",
            Attributes = new Dictionary<string, string>
            {
                [QueueAttributeName.FifoQueue] = "true",
                [QueueAttributeName.DeduplicationScope] = "messageGroup",
                [QueueAttributeName.FifoThroughputLimit] = "perMessageGroupId"
            }
        }, cancellationToken)).QueueUrl;

        // Group A: 5 messages
        for (int i = 1; i <= 5; i++)
        {
            await Sqs.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = queueUrl,
                MessageBody = $"A{i}",
                MessageGroupId = "GroupA",
                MessageDeduplicationId = $"DedupA{i}"
            }, cancellationToken);
        }

        // Group B: 2 messages
        for (int i = 1; i <= 2; i++)
        {
            await Sqs.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = queueUrl,
                MessageBody = $"B{i}",
                MessageGroupId = "GroupB",
                MessageDeduplicationId = $"DedupB{i}"
            }, cancellationToken);
        }

        // Receive messages
        var result = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 7,
            MessageSystemAttributeNames = ["MessageGroupId"]
        }, cancellationToken);

        result.Messages.Count.ShouldBe(7);

        // Verify all messages are received
        result.Messages.ShouldContain(m => m.Body == "A1");
        result.Messages.ShouldContain(m => m.Body == "A2");
        result.Messages.ShouldContain(m => m.Body == "A3");
        result.Messages.ShouldContain(m => m.Body == "A4");
        result.Messages.ShouldContain(m => m.Body == "A5");
        result.Messages.ShouldContain(m => m.Body == "B1");
        result.Messages.ShouldContain(m => m.Body == "B2");

        // Verify order within each group is preserved
        var groupAMessages = result.Messages.Where(m => m.Attributes["MessageGroupId"] == "GroupA").ToList();
        groupAMessages[0].Body.ShouldBe("A1");
        groupAMessages[1].Body.ShouldBe("A2");
        groupAMessages[2].Body.ShouldBe("A3");
        groupAMessages[3].Body.ShouldBe("A4");
        groupAMessages[4].Body.ShouldBe("A5");

        var groupBMessages = result.Messages.Where(m => m.Attributes["MessageGroupId"] == "GroupB").ToList();
        groupBMessages[0].Body.ShouldBe("B1");
        groupBMessages[1].Body.ShouldBe("B2");
    }

    [Test]
    public async Task FairQueue_WithThreeGroups_PreservesOrderWithinGroups(CancellationToken cancellationToken)
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = "test-fair-three-groups.fifo",
            Attributes = new Dictionary<string, string>
            {
                [QueueAttributeName.FifoQueue] = "true",
                [QueueAttributeName.DeduplicationScope] = "messageGroup",
                [QueueAttributeName.FifoThroughputLimit] = "perMessageGroupId"
            }
        }, cancellationToken)).QueueUrl;

        // Group A, B, and C each with 2 messages
        foreach (var group in new[] { "A", "B", "C" })
        {
            for (int i = 1; i <= 2; i++)
            {
                await Sqs.SendMessageAsync(new SendMessageRequest
                {
                    QueueUrl = queueUrl,
                    MessageBody = $"{group}{i}",
                    MessageGroupId = $"Group{group}",
                    MessageDeduplicationId = $"Dedup{group}{i}"
                }, cancellationToken);
            }
        }

        // Receive all messages
        var result = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 6,
            MessageSystemAttributeNames = ["MessageGroupId"]
        }, cancellationToken);

        result.Messages.Count.ShouldBe(6);

        // Verify all messages are received
        result.Messages.ShouldContain(m => m.Body == "A1");
        result.Messages.ShouldContain(m => m.Body == "A2");
        result.Messages.ShouldContain(m => m.Body == "B1");
        result.Messages.ShouldContain(m => m.Body == "B2");
        result.Messages.ShouldContain(m => m.Body == "C1");
        result.Messages.ShouldContain(m => m.Body == "C2");

        // Verify order within each group is preserved
        var groupAMessages = result.Messages.Where(m => m.Attributes["MessageGroupId"] == "GroupA").ToList();
        groupAMessages[0].Body.ShouldBe("A1");
        groupAMessages[1].Body.ShouldBe("A2");

        var groupBMessages = result.Messages.Where(m => m.Attributes["MessageGroupId"] == "GroupB").ToList();
        groupBMessages[0].Body.ShouldBe("B1");
        groupBMessages[1].Body.ShouldBe("B2");

        var groupCMessages = result.Messages.Where(m => m.Attributes["MessageGroupId"] == "GroupC").ToList();
        groupCMessages[0].Body.ShouldBe("C1");
        groupCMessages[1].Body.ShouldBe("C2");
    }

    [Test]
    public async Task FairQueue_DeduplicationScopedToMessageGroup(CancellationToken cancellationToken)
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = "test-fair-dedup-queue.fifo",
            Attributes = new Dictionary<string, string>
            {
                [QueueAttributeName.FifoQueue] = "true",
                [QueueAttributeName.DeduplicationScope] = "messageGroup",
                [QueueAttributeName.FifoThroughputLimit] = "perMessageGroupId"
            }
        }, cancellationToken)).QueueUrl;

        // Send messages with the same deduplication ID to different groups
        // This should work because deduplication is scoped to message group
        await Sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "Message A",
            MessageGroupId = "GroupA",
            MessageDeduplicationId = "SameDedup"
        }, cancellationToken);

        await Sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "Message B",
            MessageGroupId = "GroupB",
            MessageDeduplicationId = "SameDedup"
        }, cancellationToken);

        // Receive messages - both should be present
        var result = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 10,
            MessageSystemAttributeNames = ["MessageGroupId"]
        }, cancellationToken);

        result.Messages.Count.ShouldBe(2);
        result.Messages.ShouldContain(m => m.Body == "Message A");
        result.Messages.ShouldContain(m => m.Body == "Message B");
    }

    [Test]
    public async Task FairQueue_PreservesOrderWithinMessageGroup(CancellationToken cancellationToken)
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = "test-fair-order-queue.fifo",
            Attributes = new Dictionary<string, string>
            {
                [QueueAttributeName.FifoQueue] = "true",
                [QueueAttributeName.DeduplicationScope] = "messageGroup",
                [QueueAttributeName.FifoThroughputLimit] = "perMessageGroupId"
            }
        }, cancellationToken)).QueueUrl;

        // Send messages to Group A in a specific order
        for (int i = 1; i <= 5; i++)
        {
            await Sqs.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = queueUrl,
                MessageBody = $"A{i}",
                MessageGroupId = "GroupA",
                MessageDeduplicationId = $"DedupA{i}"
            }, cancellationToken);
        }

        // Receive messages in batches
        var firstBatch = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 2,
            MessageSystemAttributeNames = ["MessageGroupId"]
        }, cancellationToken);

        firstBatch.Messages.Count.ShouldBe(2);
        firstBatch.Messages[0].Body.ShouldBe("A1");
        firstBatch.Messages[1].Body.ShouldBe("A2");

        // Delete the first batch
        foreach (var message in firstBatch.Messages)
        {
            await Sqs.DeleteMessageAsync(queueUrl, message.ReceiptHandle, cancellationToken);
        }

        // Receive next batch - should continue in order
        var secondBatch = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 10,
            MessageSystemAttributeNames = ["MessageGroupId"]
        }, cancellationToken);

        secondBatch.Messages.Count.ShouldBe(3);
        secondBatch.Messages[0].Body.ShouldBe("A3");
        secondBatch.Messages[1].Body.ShouldBe("A4");
        secondBatch.Messages[2].Body.ShouldBe("A5");
    }
}

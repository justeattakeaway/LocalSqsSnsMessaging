using Amazon.SQS;
using Amazon.SQS.Model;

namespace LocalSqsSnsMessaging.Tests;

public abstract class SqsFifoTests
{
    protected IAmazonSQS Sqs = null!;
    protected string AccountId = null!;

    [Test]
    public async Task CreateFifoQueue_SetsCorrectAttributes(CancellationToken cancellationToken)
    {
        var queueName = "test-fifo-queue.fifo";
        var createQueueResponse = await Sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = queueName,
            Attributes = new Dictionary<string, string>
            {
                [QueueAttributeName.FifoQueue] = "true"
            }
        }, cancellationToken);

        var attributes = await Sqs.GetQueueAttributesAsync(createQueueResponse.QueueUrl, [QueueAttributeName.FifoQueue], cancellationToken);

        await Assert.That(bool.Parse(attributes.Attributes[QueueAttributeName.FifoQueue])).IsTrue();
    }

    [Test]
    public async Task SendMessageToFifoQueue_RequiresMessageGroupId(CancellationToken cancellationToken)
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = "test-fifo-queue.fifo",
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
            QueueName = "test-fifo-queue.fifo",
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

        // Receive messages
        var receiveRequest = new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 10
        };

        var result = await Sqs.ReceiveMessageAsync(receiveRequest, cancellationToken);

        await Assert.That(result.Messages.Count).IsEqualTo(3);
        await Assert.That(result.Messages[0].Body).IsEqualTo("Message 1 Group A");
        await Assert.That(result.Messages[1].Body).IsEqualTo("Message 2 Group A");
        await Assert.That(result.Messages[2].Body).IsEqualTo("Message 1 Group B");
    }

    [Test]
    public async Task FifoQueue_MessageDeduplication(CancellationToken cancellationToken)
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = "test-fifo-queue.fifo",
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

        // Receive messages
        var receiveRequest = new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 10
        };

        var result = await Sqs.ReceiveMessageAsync(receiveRequest, cancellationToken);

        await Assert.That(result.Messages.Count).IsEqualTo(2);
        await Assert.That(result.Messages[0].Body).IsEqualTo("Duplicate Message");
        await Assert.That(result.Messages[1].Body).IsEqualTo("Unique Message");
    }

    [Test]
    public async Task FifoQueue_ContentBasedDeduplication(CancellationToken cancellationToken)
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = "test-content-dedup-queue.fifo",
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

        // Receive messages
        var receiveRequest = new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 10
        };

        var result = await Sqs.ReceiveMessageAsync(receiveRequest, cancellationToken);

        await Assert.That(result.Messages.Count).IsEqualTo(2);
        await Assert.That(result.Messages[0].Body).IsEqualTo("Duplicate Content");
        await Assert.That(result.Messages[1].Body).IsEqualTo("Unique Content");
    }

    [Test]
    public async Task FifoQueue_HighThroughputMode(CancellationToken cancellationToken)
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = "test-high-throughput-queue.fifo",
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

        // Receive messages
        var receiveRequest = new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 10
        };

        var result = await Sqs.ReceiveMessageAsync(receiveRequest, cancellationToken);

        await Assert.That(result.Messages.Count).IsEqualTo(10);
        // Verify that messages from both groups are interleaved
        await Assert.That(result.Messages).Contains(m => m.Body.Contains("Group A", StringComparison.Ordinal));
        await Assert.That(result.Messages).Contains(m => m.Body.Contains("Group B", StringComparison.Ordinal));
    }

    protected abstract Task AdvanceTime(TimeSpan timeSpan);
}

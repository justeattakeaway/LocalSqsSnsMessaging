using Amazon;
using Amazon.SQS;
using Amazon.SQS.Model;
using Xunit;

namespace LocalSqsSnsMessaging.Tests;

public abstract class SqsFifoTests
{
    protected IAmazonSQS Sqs = null!;
    protected string AccountId = null!;

    [Fact]
    public async Task CreateFifoQueue_SetsCorrectAttributes()
    {
        var queueName = "test-fifo-queue.fifo";
        var createQueueResponse = await Sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = queueName,
            Attributes = new Dictionary<string, string>
            {
                [QueueAttributeName.FifoQueue] = "true"
            }
        }, TestContext.Current.CancellationToken);

        var attributes = await Sqs.GetQueueAttributesAsync(createQueueResponse.QueueUrl, [QueueAttributeName.FifoQueue], TestContext.Current.CancellationToken);

        Assert.True(bool.Parse(attributes.Attributes[QueueAttributeName.FifoQueue]));
    }

    [Fact]
    public async Task SendMessageToFifoQueue_RequiresMessageGroupId()
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = "test-fifo-queue.fifo",
            Attributes = new Dictionary<string, string> { [QueueAttributeName.FifoQueue] = "true" }
        }, TestContext.Current.CancellationToken)).QueueUrl;

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await Sqs.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = queueUrl,
                MessageBody = "Test Message"
                // MessageGroupId is missing
            }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FifoQueue_EnforcesMessageGroupOrdering()
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = "test-fifo-queue.fifo",
            Attributes = new Dictionary<string, string> { [QueueAttributeName.FifoQueue] = "true" }
        }, TestContext.Current.CancellationToken)).QueueUrl;

        // Send messages to two different message groups
        await Sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "Message 1 Group A",
            MessageGroupId = "GroupA",
            MessageDeduplicationId = "Dedup1A"
        }, TestContext.Current.CancellationToken);

        await Sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "Message 2 Group A",
            MessageGroupId = "GroupA",
            MessageDeduplicationId = "Dedup2A"
        }, TestContext.Current.CancellationToken);

        await Sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "Message 1 Group B",
            MessageGroupId = "GroupB",
            MessageDeduplicationId = "Dedup1B"
        }, TestContext.Current.CancellationToken);

        // Receive messages
        var receiveRequest = new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 10
        };

        var result = await Sqs.ReceiveMessageAsync(receiveRequest, TestContext.Current.CancellationToken);

        Assert.Equal(3, result.Messages.Count);
        Assert.Equal("Message 1 Group A", result.Messages[0].Body);
        Assert.Equal("Message 2 Group A", result.Messages[1].Body);
        Assert.Equal("Message 1 Group B", result.Messages[2].Body);
    }

    [Fact]
    public async Task FifoQueue_MessageDeduplication()
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = "test-fifo-queue.fifo",
            Attributes = new Dictionary<string, string>
            {
                [QueueAttributeName.FifoQueue] = "true",
                [QueueAttributeName.ContentBasedDeduplication] = "false"
            }
        }, TestContext.Current.CancellationToken)).QueueUrl;

        // Send two messages with the same deduplication ID
        await Sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "Duplicate Message",
            MessageGroupId = "GroupA",
            MessageDeduplicationId = "DuplicateDedup"
        }, TestContext.Current.CancellationToken);

        await Sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "Duplicate Message",
            MessageGroupId = "GroupA",
            MessageDeduplicationId = "DuplicateDedup"
        }, TestContext.Current.CancellationToken);

        // Send a message with a different deduplication ID
        await Sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "Unique Message",
            MessageGroupId = "GroupA",
            MessageDeduplicationId = "UniqueDedup"
        }, TestContext.Current.CancellationToken);

        // Receive messages
        var receiveRequest = new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 10
        };

        var result = await Sqs.ReceiveMessageAsync(receiveRequest, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Messages.Count);
        Assert.Equal("Duplicate Message", result.Messages[0].Body);
        Assert.Equal("Unique Message", result.Messages[1].Body);
    }

    [Fact]
    public async Task FifoQueue_ContentBasedDeduplication()
    {
        var queueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = "test-content-dedup-queue.fifo",
            Attributes = new Dictionary<string, string>
            {
                [QueueAttributeName.FifoQueue] = "true",
                [QueueAttributeName.ContentBasedDeduplication] = "true"
            }
        }, TestContext.Current.CancellationToken)).QueueUrl;

        // Send two identical messages without specifying MessageDeduplicationId
        await Sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "Duplicate Content",
            MessageGroupId = "GroupA"
        }, TestContext.Current.CancellationToken);

        await Sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "Duplicate Content",
            MessageGroupId = "GroupA"
        }, TestContext.Current.CancellationToken);

        // Send a different message
        await Sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "Unique Content",
            MessageGroupId = "GroupA"
        }, TestContext.Current.CancellationToken);

        // Receive messages
        var receiveRequest = new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 10
        };

        var result = await Sqs.ReceiveMessageAsync(receiveRequest, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Messages.Count);
        Assert.Equal("Duplicate Content", result.Messages[0].Body);
        Assert.Equal("Unique Content", result.Messages[1].Body);
    }

    [Fact]
    public async Task FifoQueue_HighThroughputMode()
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
        }, TestContext.Current.CancellationToken)).QueueUrl;

        // Send messages to different message groups
        for (int i = 0; i < 5; i++)
        {
            await Sqs.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = queueUrl,
                MessageBody = $"Message {i} Group A",
                MessageGroupId = "GroupA",
                MessageDeduplicationId = $"DedupA{i}"
            }, TestContext.Current.CancellationToken);

            await Sqs.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = queueUrl,
                MessageBody = $"Message {i} Group B",
                MessageGroupId = "GroupB",
                MessageDeduplicationId = $"DedupB{i}"
            }, TestContext.Current.CancellationToken);
        }

        // Receive messages
        var receiveRequest = new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 10
        };

        var result = await Sqs.ReceiveMessageAsync(receiveRequest, TestContext.Current.CancellationToken);

        Assert.Equal(10, result.Messages.Count);
        // Verify that messages from both groups are interleaved
        Assert.Contains(result.Messages, m => m.Body.Contains("Group A", StringComparison.Ordinal));
        Assert.Contains(result.Messages, m => m.Body.Contains("Group B", StringComparison.Ordinal));
    }

    protected abstract Task AdvanceTime(TimeSpan timeSpan);
}

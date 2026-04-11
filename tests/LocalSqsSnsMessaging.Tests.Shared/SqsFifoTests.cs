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

        bool.Parse(attributes.Attributes[QueueAttributeName.FifoQueue]).ShouldBeTrue();
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

        // Receive all messages (may require multiple polls against real SQS/moto)
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

        // Receive all messages (may require multiple polls against real SQS/moto)
        var messages = await ReceiveAllMessagesAsync(Sqs, queueUrl, 10, cancellationToken);

        messages.Count.ShouldBe(10);
        // Verify that messages from both groups are present
        messages.ShouldContain(m => m.Body.Contains("Group A", StringComparison.Ordinal));
        messages.ShouldContain(m => m.Body.Contains("Group B", StringComparison.Ordinal));
    }
}

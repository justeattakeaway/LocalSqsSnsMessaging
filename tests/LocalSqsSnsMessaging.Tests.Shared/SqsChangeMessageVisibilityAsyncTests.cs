using Amazon.SQS;
using Amazon.SQS.Model;

namespace LocalSqsSnsMessaging.Tests;

public abstract class SqsChangeMessageVisibilityAsyncTests
{
    protected IAmazonSQS Sqs = null!;
    private string _queueUrl = null!;

    private async Task SetupQueueAndMessage()
    {
        var createQueueResponse = await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test-queue" });
        _queueUrl = createQueueResponse.QueueUrl;

        await Sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = _queueUrl,
            MessageBody = "Test message"
        });
    }

    [Fact]
    public async Task ChangeMessageVisibilityAsync_ValidRequest_ChangesVisibilityTimeout()
    {
        await SetupQueueAndMessage();

        // Receive the message
        var receiveResult = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = _queueUrl }, TestContext.Current.CancellationToken);
        var message = Assert.Single(receiveResult.Messages);

        // Change visibility timeout to 10 seconds
        await Sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest
        {
            QueueUrl = _queueUrl,
            ReceiptHandle = message.ReceiptHandle,
            VisibilityTimeout = 10
        }, TestContext.Current.CancellationToken);

        // Try to receive the message again immediately (should fail)
        var immediateReceiveResult = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = _queueUrl }, TestContext.Current.CancellationToken);
        Assert.Empty(immediateReceiveResult.Messages);

        // Advance time by 11 seconds
        await AdvanceTime(TimeSpan.FromSeconds(11));

        // Now we should be able to receive the message
        var finalReceiveResult = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = _queueUrl }, TestContext.Current.CancellationToken);
        Assert.Single(finalReceiveResult.Messages);
    }

    [Fact]
    public async Task ChangeMessageVisibilityAsync_SetToZero_MakesMessageImmediatelyAvailable()
    {
        await SetupQueueAndMessage();

        // Receive the message
        var receiveResult = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = _queueUrl }, TestContext.Current.CancellationToken);
        var message = Assert.Single(receiveResult.Messages);

        // Change visibility timeout to 0 seconds
        await Sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest
        {
            QueueUrl = _queueUrl,
            ReceiptHandle = message.ReceiptHandle,
            VisibilityTimeout = 0
        }, TestContext.Current.CancellationToken);

        // Try to receive the message again immediately (should succeed)
        var immediateReceiveResult = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = _queueUrl }, TestContext.Current.CancellationToken);
        Assert.Single(immediateReceiveResult.Messages);
    }

    [Fact]
    public async Task ChangeMessageVisibilityAsync_InvalidReceiptHandle_ThrowsException()
    {
        await SetupQueueAndMessage();

        // Attempt to change visibility with an invalid receipt handle
        await Assert.ThrowsAsync<ReceiptHandleIsInvalidException>(() =>
            Sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest
            {
                QueueUrl = _queueUrl,
                ReceiptHandle = "invalid-receipt-handle",
                VisibilityTimeout = 10
            }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ChangeMessageVisibilityAsync_MessageNotInFlight_ThrowsException()
    {
        await SetupQueueAndMessage();

        // Receive the message
        var receiveResult = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = _queueUrl, VisibilityTimeout = 5}, TestContext.Current.CancellationToken);
        var message = Assert.Single(receiveResult.Messages);

        // Wait for the visibility timeout to expire
        await AdvanceTime(TimeSpan.FromSeconds(10)); // Assuming default is 30 seconds

        // Attempt to change visibility of the message that's no longer in flight
        // Should complete without throwing
        await Sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest
        {
            QueueUrl = _queueUrl,
            ReceiptHandle = message.ReceiptHandle,
            VisibilityTimeout = 10
        }, TestContext.Current.CancellationToken);

        var secondReceiveResult = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = _queueUrl }, TestContext.Current.CancellationToken);
        Assert.Single(secondReceiveResult.Messages);
    }

    [Fact]
    public async Task ChangeMessageVisibilityAsync_ChangeMultipleTimes_LastChangeApplies()
    {
        await SetupQueueAndMessage();

        // Receive the message
        var receiveResult = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = _queueUrl }, TestContext.Current.CancellationToken);
        var message = Assert.Single(receiveResult.Messages);

        // Change visibility timeout to 30 seconds
        await Sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest
        {
            QueueUrl = _queueUrl,
            ReceiptHandle = message.ReceiptHandle,
            VisibilityTimeout = 30
        }, TestContext.Current.CancellationToken);

        // Change visibility timeout to 10 seconds
        await Sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest
        {
            QueueUrl = _queueUrl,
            ReceiptHandle = message.ReceiptHandle,
            VisibilityTimeout = 10
        }, TestContext.Current.CancellationToken);

        // Advance time by 11 seconds
        await AdvanceTime(TimeSpan.FromSeconds(11));

        // Now we should be able to receive the message (10 second timeout applies, not 30)
        var finalReceiveResult = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = _queueUrl }, TestContext.Current.CancellationToken);
        Assert.Single(finalReceiveResult.Messages);
    }

    protected abstract Task AdvanceTime(TimeSpan timeSpan);
}
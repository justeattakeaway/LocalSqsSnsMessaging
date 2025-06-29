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

    [Test, Category("TimeBasedTests")]
    public async Task ChangeMessageVisibilityAsync_ValidRequest_ChangesVisibilityTimeout(CancellationToken cancellationToken)
    {
        await SetupQueueAndMessage();

        // Receive the message
        var receiveResult = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = _queueUrl }, cancellationToken);
        var message = await Assert.That(receiveResult.Messages).HasSingleItem();

        // Change visibility timeout to 10 seconds
        await Sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest
        {
            QueueUrl = _queueUrl,
            ReceiptHandle = message!.ReceiptHandle,
            VisibilityTimeout = 10
        }, cancellationToken);

        // Try to receive the message again immediately (should fail)
        var immediateReceiveResult = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = _queueUrl }, cancellationToken);
        immediateReceiveResult.Messages.ShouldBeUninitialized();

        // Advance time by 11 seconds
        await AdvanceTime(TimeSpan.FromSeconds(11));

        // Now we should be able to receive the message
        var finalReceiveResult = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = _queueUrl }, cancellationToken);
        await Assert.That(finalReceiveResult.Messages).HasSingleItem();
    }

    [Test]
    public async Task ChangeMessageVisibilityAsync_SetToZero_MakesMessageImmediatelyAvailable(CancellationToken cancellationToken)
    {
        await SetupQueueAndMessage();

        // Receive the message
        var receiveResult = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = _queueUrl }, cancellationToken);
        var message = await Assert.That(receiveResult.Messages).HasSingleItem();

        // Change visibility timeout to 0 seconds
        await Sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest
        {
            QueueUrl = _queueUrl,
            ReceiptHandle = message!.ReceiptHandle,
            VisibilityTimeout = 0
        }, cancellationToken);

        // Try to receive the message again immediately (should succeed)
        var immediateReceiveResult = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = _queueUrl }, cancellationToken);
        await Assert.That(immediateReceiveResult.Messages).HasSingleItem();
    }

    [Test]
    public async Task ChangeMessageVisibilityAsync_InvalidReceiptHandle_ThrowsException(CancellationToken cancellationToken)
    {
        await SetupQueueAndMessage();

        // Attempt to change visibility with an invalid receipt handle
        await Assert.ThrowsAsync<ReceiptHandleIsInvalidException>(() =>
            Sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest
            {
                QueueUrl = _queueUrl,
                ReceiptHandle = "invalid-receipt-handle",
                VisibilityTimeout = 10
            }, cancellationToken));
    }

    [Test, Category("TimeBasedTests")]
    public async Task ChangeMessageVisibilityAsync_MessageNotInFlight_ThrowsException(CancellationToken cancellationToken)
    {
        await SetupQueueAndMessage();

        // Receive the message
        var receiveResult = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = _queueUrl, VisibilityTimeout = 5}, cancellationToken);
        var message = await Assert.That(receiveResult.Messages).HasSingleItem();

        // Wait for the visibility timeout to expire
        await AdvanceTime(TimeSpan.FromSeconds(10)); // Assuming default is 30 seconds

        // Attempt to change visibility of the message that's no longer in flight
        // Should complete without throwing
        await Sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest
        {
            QueueUrl = _queueUrl,
            ReceiptHandle = message!.ReceiptHandle,
            VisibilityTimeout = 10
        }, cancellationToken);

        var secondReceiveResult = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = _queueUrl }, cancellationToken);
        await Assert.That(secondReceiveResult.Messages).HasSingleItem();
    }

    [Test, Category("TimeBasedTests")]
    public async Task ChangeMessageVisibilityAsync_ChangeMultipleTimes_LastChangeApplies(CancellationToken cancellationToken)
    {
        await SetupQueueAndMessage();

        // Receive the message
        var receiveResult = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = _queueUrl }, cancellationToken);
        var message = await Assert.That(receiveResult.Messages).HasSingleItem();

        // Change visibility timeout to 30 seconds
        await Sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest
        {
            QueueUrl = _queueUrl,
            ReceiptHandle = message!.ReceiptHandle,
            VisibilityTimeout = 30
        }, cancellationToken);

        // Change visibility timeout to 10 seconds
        await Sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest
        {
            QueueUrl = _queueUrl,
            ReceiptHandle = message.ReceiptHandle,
            VisibilityTimeout = 10
        }, cancellationToken);

        // Advance time by 11 seconds
        await AdvanceTime(TimeSpan.FromSeconds(11));

        // Now we should be able to receive the message (10 second timeout applies, not 30)
        var finalReceiveResult = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = _queueUrl }, cancellationToken);
        await Assert.That(finalReceiveResult.Messages).HasSingleItem();
    }

    protected abstract Task AdvanceTime(TimeSpan timeSpan);
}

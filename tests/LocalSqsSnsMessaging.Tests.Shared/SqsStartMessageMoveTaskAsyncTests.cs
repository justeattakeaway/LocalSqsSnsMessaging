using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.SQS;
using Amazon.SQS.Model;
using Shouldly;
using ResourceNotFoundException = Amazon.SQS.Model.ResourceNotFoundException;

namespace LocalSqsSnsMessaging.Tests;

public abstract class SqsStartMessageMoveTaskAsyncTests : WaitingTestBase
{
    protected IAmazonSQS Sqs = null!;
    protected string AccountId = null!;
    private string _errorQueueArn = null!;
    private string _errorQueueUrl = null!;
    private string _mainQueueArn = null!;
    private string _mainQueueUrl = null!;

    private async Task SetupQueuesAndMessage()
    {
        // Create main queue
        var createMainQueueResponse = await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "main-queue" });
        _mainQueueUrl = createMainQueueResponse.QueueUrl;
        _mainQueueArn = await GetQueueArnFromUrl(_mainQueueUrl);

        // Create source queue (this will be our DLQ)
        var createSourceQueueResponse = await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "source-queue" });
        _errorQueueUrl = createSourceQueueResponse.QueueUrl;
        _errorQueueArn = await GetQueueArnFromUrl(_errorQueueUrl);

        // Set source queue as the dead letter queue for the main queue
        var redrivePolicy = new JsonObject
        {
            ["deadLetterTargetArn"] = JsonValue.Create(_errorQueueArn),
            ["maxReceiveCount"] = JsonValue.Create(1)
        };
        await Sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = _mainQueueUrl,
            Attributes = new Dictionary<string, string>
            {
                ["RedrivePolicy"] = redrivePolicy.ToJsonString()
            }
        });

        // Send a message to the main queue
        await Sqs.SendMessageBatchAsync(new SendMessageBatchRequest
        {
            QueueUrl = _mainQueueUrl,
            Entries =
            [
                new SendMessageBatchRequestEntry { Id = "1", MessageBody = "Test message 1" },
                new SendMessageBatchRequestEntry { Id = "2", MessageBody = "Test message 2" },
                new SendMessageBatchRequestEntry { Id = "3", MessageBody = "Test message 3" },
                new SendMessageBatchRequestEntry { Id = "4", MessageBody = "Test message 4" }
            ]
        });

        // Receive the messages twice from the main queue to trigger the DLQ
        var firstMessages = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            { QueueUrl = _mainQueueUrl, WaitTimeSeconds = 5, MaxNumberOfMessages = 10});

        await Sqs.ChangeMessageVisibilityBatchAsync(new ChangeMessageVisibilityBatchRequest
        {
            QueueUrl = _mainQueueUrl,
            Entries = firstMessages.Messages.Select(m => new ChangeMessageVisibilityBatchRequestEntry
            {
                Id = m.MessageId,
                ReceiptHandle = m.ReceiptHandle,
                VisibilityTimeout = 0
            }).ToList()
        });

        var secondMessages = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = _mainQueueUrl });
        //await AdvanceTime(TimeSpan.FromSeconds(1));
        secondMessages.Messages.ShouldBeEmptyAwsCollection();
    }

    private async Task<string> GetQueueArnFromUrl(string queueUrl)
    {
        var attrResponse = await Sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            AttributeNames = ["QueueArn"]
        });
        return attrResponse.Attributes["QueueArn"];
    }

    [Test, Category(TimeBased)]
    public async Task StartMessageMoveTaskAsync_ValidRequest_MovesMessage(CancellationToken cancellationToken)
    {
        await SetupQueuesAndMessage();

        // Start the message move task
        var startMoveResponse = await Sqs.StartMessageMoveTaskAsync(new StartMessageMoveTaskRequest
        {
            SourceArn = _errorQueueArn,
            DestinationArn = _mainQueueArn,
            MaxNumberOfMessagesPerSecond = 10
        }, cancellationToken);

        startMoveResponse.TaskHandle.ShouldNotBeNull();

        // Wait for the move to complete
        await WaitAsync(TimeSpan.FromSeconds(2));

        // Check that the message is no longer in the source queue (DLQ)
        var sourceReceiveResult = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = _errorQueueUrl }, cancellationToken);
        sourceReceiveResult.Messages.ShouldBeEmptyAwsCollection();

        // Check that the message is now in the main queue
        var mainReceiveResult = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = _mainQueueUrl, MaxNumberOfMessages = 10}, cancellationToken);
        mainReceiveResult.Messages.Count.ShouldBe(4);
    }

    [Test, Category(TimeBased)]
    public async Task StartMessageMoveTaskAsync_NonDLQSource_ThrowsException(CancellationToken cancellationToken)
    {
        await SetupQueuesAndMessage();

        // Create a new queue that is not configured as a DLQ
        var createNonDlqResponse = await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "non-dlq" }, cancellationToken);
        var nonDlqArn = await GetQueueArnFromUrl(createNonDlqResponse.QueueUrl);

        // Create destination queue
        var createDestQueueResponse = await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "destination-queue" }, cancellationToken);
        var destinationQueueArn = await GetQueueArnFromUrl(createDestQueueResponse.QueueUrl);

        await Assert.ThrowsAsync<Exception>(() =>
            Sqs.StartMessageMoveTaskAsync(new StartMessageMoveTaskRequest
            {
                SourceArn = nonDlqArn, // Using non-DLQ as source
                DestinationArn = destinationQueueArn,
                MaxNumberOfMessagesPerSecond = 10
            }, cancellationToken));
    }

    [Test, Category(TimeBased)]
    public async Task StartMessageMoveTaskAsync_InvalidDestinationQueue_ThrowsException(CancellationToken cancellationToken)
    {
        await SetupQueuesAndMessage();

        await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            Sqs.StartMessageMoveTaskAsync(new StartMessageMoveTaskRequest
            {
                SourceArn = _errorQueueArn,
                DestinationArn = $"arn:aws:sqs:us-east-1:{AccountId}:invalid-destination-queue",
                MaxNumberOfMessagesPerSecond = 10
            }, cancellationToken));
    }

    [Test, Category(TimeBased)]
    public async Task StartMessageMoveTaskAsync_EmptyDLQ_NoMessagesMoved(CancellationToken cancellationToken)
    {
        await SetupQueuesAndMessage();

        await WaitAsync(TimeSpan.FromSeconds(1));

        // Empty the source queue (DLQ)
        var receiveResult = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = _errorQueueUrl, MaxNumberOfMessages = 10}, cancellationToken);
        foreach (var message in receiveResult.Messages)
        {
            await Sqs.DeleteMessageAsync(_errorQueueUrl, message.ReceiptHandle, cancellationToken);
        }

        // Start the message move task
        var startMoveResponse = await Sqs.StartMessageMoveTaskAsync(new StartMessageMoveTaskRequest
        {
            SourceArn = _errorQueueArn,
            DestinationArn = _mainQueueArn,
            MaxNumberOfMessagesPerSecond = 10
        }, cancellationToken);

        startMoveResponse.TaskHandle.ShouldNotBeNull();

        // Wait for the move to complete
        await WaitAsync(TimeSpan.FromSeconds(1));

        // Check that the main queue is still empty
        var mainReceiveResult = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = _mainQueueUrl }, cancellationToken);
        mainReceiveResult.Messages.ShouldBeEmptyAwsCollection();
    }

    [Test, Category(TimeBased)]
    public async Task StartMessageMoveTaskAsync_MaxNumberOfMessagesPerSecond_RespectsLimit(CancellationToken cancellationToken)
    {
        await SetupQueuesAndMessage();

        // Add more messages to the source queue (DLQ)
        for (int i = 0; i < 5; i++)
        {
            await Sqs.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = _errorQueueUrl,
                MessageBody = $"Test message {i + 2}"
            }, cancellationToken);
        }

        // Start the message move task with a limit of 3 messages per second
        var startMoveResponse = await Sqs.StartMessageMoveTaskAsync(new StartMessageMoveTaskRequest
        {
            SourceArn = _errorQueueArn,
            DestinationArn = _mainQueueArn,
            MaxNumberOfMessagesPerSecond = 3
        }, cancellationToken);

        startMoveResponse.TaskHandle.ShouldNotBeNull();

        // Wait for 1 second (should move approximately 3 messages)
        await WaitAsync(TimeSpan.FromSeconds(1));

        // Check that only about 3 messages were moved to the main queue
        var mainReceiveResult = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = _mainQueueUrl,
            MaxNumberOfMessages = 10
        }, cancellationToken);
        mainReceiveResult.Messages.Count.ShouldBeInRange(2, 4); // Allow for some flexibility due to timing

        // Check that about 3 messages remain in the source queue (DLQ)
        var sourceReceiveResult = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = _errorQueueUrl,
            MaxNumberOfMessages = 10
        }, cancellationToken);
        sourceReceiveResult.Messages.Count.ShouldBeInRange(4, 6); // Allow for some flexibility due to timing
    }

    [Test, Category(TimeBased)]
    public async Task StartMessageMoveTaskAsync_NoDestinationArn_MovesToOriginalSource(CancellationToken cancellationToken)
    {
        await SetupQueuesAndMessage();

        // Start the message move task without specifying a destination ARN
        var startMoveResponse = await Sqs.StartMessageMoveTaskAsync(new StartMessageMoveTaskRequest
        {
            SourceArn = _errorQueueArn,
            MaxNumberOfMessagesPerSecond = 10
        }, cancellationToken);

        startMoveResponse.TaskHandle.ShouldNotBeNull();

        // Wait for the move to complete
        await WaitAsync(TimeSpan.FromSeconds(5));

        // Check that the message is now in the main queue (original source)
        var mainReceiveResult = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = _mainQueueUrl }, cancellationToken);
        mainReceiveResult.Messages.ShouldHaveSingleItem();

        // Check that the source queue (DLQ) is empty
        var sourceReceiveResult = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = _errorQueueUrl }, cancellationToken);
        sourceReceiveResult.Messages.ShouldBeEmptyAwsCollection();
    }

    [Test, Category(TimeBased)]
    public async Task CancelMessageMoveTaskAsync_ValidTaskHandle_StopsTask(CancellationToken cancellationToken)
    {
        await SetupQueuesAndMessage();

        // Start the message move task
        var startMoveResponse = await Sqs.StartMessageMoveTaskAsync(new StartMessageMoveTaskRequest
        {
            SourceArn = _errorQueueArn,
            DestinationArn = _mainQueueArn,
            MaxNumberOfMessagesPerSecond = 1 // Set a low rate to ensure we can stop before completion
        }, cancellationToken);

        // Cancel the task immediately
        await Sqs.CancelMessageMoveTaskAsync(new CancelMessageMoveTaskRequest
        {
            TaskHandle = startMoveResponse.TaskHandle
        }, cancellationToken);

        // Wait a moment to ensure the task has time to stop
        await WaitAsync(TimeSpan.FromSeconds(5));

        // Check that the message is still in the source queue (DLQ)
        var sourceReceiveResult = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = _errorQueueUrl }, cancellationToken);
        sourceReceiveResult.Messages.ShouldNotBeEmpty();
    }

    [Test, Category(TimeBased)]
    public async Task StartingTwoMessageMoveTasksForTheSameQueue_Throws(CancellationToken cancellationToken)
    {
        await SetupQueuesAndMessage();

        // Start two message move tasks
        var task1 = await Sqs.StartMessageMoveTaskAsync(new StartMessageMoveTaskRequest
        {
            SourceArn = _errorQueueArn,
            DestinationArn = _mainQueueArn,
            MaxNumberOfMessagesPerSecond = 10
        }, cancellationToken);

        await Assert.ThrowsAsync<Exception>(async () =>
        {
            var task2 = await Sqs.StartMessageMoveTaskAsync(new StartMessageMoveTaskRequest
            {
                SourceArn = _errorQueueArn,
                DestinationArn = _mainQueueArn,
                MaxNumberOfMessagesPerSecond = 5
            }, cancellationToken);
        });
    }

    [Test, Category(TimeBased)]
    public async Task ListMessageMoveTasks_ReturnsAllActiveTasks(CancellationToken cancellationToken)
    {
        await SetupQueuesAndMessage();

        // Create a second DLQ and main queue
        var createSecondDlqResponse = await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "second-dlq" }, cancellationToken);
        var secondDlqUrl = createSecondDlqResponse.QueueUrl;
        var secondDlqArn = await GetQueueArnFromUrl(secondDlqUrl);

        var createSecondMainQueueResponse = await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "second-main-queue" }, cancellationToken);
        var secondMainQueueUrl = createSecondMainQueueResponse.QueueUrl;
        var secondMainQueueArn = await GetQueueArnFromUrl(secondMainQueueUrl);

        // Set up the second DLQ as a dead letter queue for the second main queue
        var redrivePolicy = new Dictionary<string, string>
        {
            ["deadLetterTargetArn"] = secondDlqArn,
            ["maxReceiveCount"] = "5"
        };
        await Sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = secondMainQueueUrl,
            Attributes = new Dictionary<string, string>
            {
                ["RedrivePolicy"] = JsonSerializer.Serialize(redrivePolicy)
            }
        }, cancellationToken);

        // Start two message move tasks, one for each DLQ
        var task1 = await Sqs.StartMessageMoveTaskAsync(new StartMessageMoveTaskRequest
        {
            SourceArn = _errorQueueArn,
            DestinationArn = _mainQueueArn,
            MaxNumberOfMessagesPerSecond = 10
        }, cancellationToken);

        // List the active tasks
        var listTasksResponse = await Sqs.ListMessageMoveTasksAsync(new ListMessageMoveTasksRequest
        {
            SourceArn = _errorQueueArn
        }, cancellationToken);

        var task1Info = listTasksResponse.Results.ShouldHaveSingleItem();

        // Verify the details of the task
        _errorQueueArn.ShouldBe(task1Info.SourceArn);
        _mainQueueArn.ShouldBe(task1Info.DestinationArn);
        task1Info.MaxNumberOfMessagesPerSecond.ShouldBe(10);
    }
}

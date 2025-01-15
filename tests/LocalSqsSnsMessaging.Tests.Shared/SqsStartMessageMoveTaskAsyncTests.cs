using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.SQS;
using Amazon.SQS.Model;
using Shouldly;
using ResourceNotFoundException = Amazon.SQS.Model.ResourceNotFoundException;

namespace LocalSqsSnsMessaging.Tests;

public abstract class SqsStartMessageMoveTaskAsyncTests
{
    protected IAmazonSQS Sqs = null!;
    protected string AccountId = null!;
    private string _errorQueueArn = null!;
    private string _errorQueueUrl = null!;
    private string _mainQueueArn = null!;
    private string _mainQueueUrl = null!;

    protected abstract Task AdvanceTime(TimeSpan timeSpan);

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
        await AdvanceTime(TimeSpan.FromSeconds(1));
        Assert.Empty(secondMessages.Messages);
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

    [Fact, Trait("Category", "TimeBasedTests")]
    public async Task StartMessageMoveTaskAsync_ValidRequest_MovesMessage()
    {
        await SetupQueuesAndMessage();

        // Start the message move task
        var startMoveResponse = await Sqs.StartMessageMoveTaskAsync(new StartMessageMoveTaskRequest
        {
            SourceArn = _errorQueueArn,
            DestinationArn = _mainQueueArn,
            MaxNumberOfMessagesPerSecond = 10
        }, TestContext.Current.CancellationToken);

        Assert.NotNull(startMoveResponse.TaskHandle);

        // Wait for the move to complete
        await AdvanceTime(TimeSpan.FromSeconds(2));

        // Check that the message is no longer in the source queue (DLQ)
        var sourceReceiveResult = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = _errorQueueUrl }, TestContext.Current.CancellationToken);
        Assert.Empty(sourceReceiveResult.Messages);

        // Check that the message is now in the main queue
        var mainReceiveResult = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = _mainQueueUrl, MaxNumberOfMessages = 10}, TestContext.Current.CancellationToken);
        mainReceiveResult.Messages.Count.ShouldBe(4);
    }

    [Fact, Trait("Category", "TimeBasedTests")]
    public async Task StartMessageMoveTaskAsync_NonDLQSource_ThrowsException()
    {
        await SetupQueuesAndMessage();

        // Create a new queue that is not configured as a DLQ
        var createNonDlqResponse = await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "non-dlq" }, TestContext.Current.CancellationToken);
        var nonDlqArn = await GetQueueArnFromUrl(createNonDlqResponse.QueueUrl);
        
        // Create destination queue
        var createDestQueueResponse = await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "destination-queue" }, TestContext.Current.CancellationToken);
        var destinationQueueArn = await GetQueueArnFromUrl(createDestQueueResponse.QueueUrl);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            Sqs.StartMessageMoveTaskAsync(new StartMessageMoveTaskRequest
            {
                SourceArn = nonDlqArn, // Using non-DLQ as source
                DestinationArn = destinationQueueArn,
                MaxNumberOfMessagesPerSecond = 10
            }, TestContext.Current.CancellationToken));
    }
    
    [Fact, Trait("Category", "TimeBasedTests")]
    public async Task StartMessageMoveTaskAsync_InvalidDestinationQueue_ThrowsException()
    {
        await SetupQueuesAndMessage();

        await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            Sqs.StartMessageMoveTaskAsync(new StartMessageMoveTaskRequest
            {
                SourceArn = _errorQueueArn,
                DestinationArn = $"arn:aws:sqs:us-east-1:{AccountId}:invalid-destination-queue",
                MaxNumberOfMessagesPerSecond = 10
            }, TestContext.Current.CancellationToken));
    }

    [Fact, Trait("Category", "TimeBasedTests")]
    public async Task StartMessageMoveTaskAsync_EmptyDLQ_NoMessagesMoved()
    {
        await SetupQueuesAndMessage();
        
        await AdvanceTime(TimeSpan.FromSeconds(1));

        // Empty the source queue (DLQ)
        var receiveResult = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = _errorQueueUrl, MaxNumberOfMessages = 10}, TestContext.Current.CancellationToken);
        foreach (var message in receiveResult.Messages)
        {
            await Sqs.DeleteMessageAsync(_errorQueueUrl, message.ReceiptHandle, TestContext.Current.CancellationToken);
        }

        // Start the message move task
        var startMoveResponse = await Sqs.StartMessageMoveTaskAsync(new StartMessageMoveTaskRequest
        {
            SourceArn = _errorQueueArn,
            DestinationArn = _mainQueueArn,
            MaxNumberOfMessagesPerSecond = 10
        }, TestContext.Current.CancellationToken);

        Assert.NotNull(startMoveResponse.TaskHandle);

        // Wait for the move to complete
        await AdvanceTime(TimeSpan.FromSeconds(1));

        // Check that the main queue is still empty
        var mainReceiveResult = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = _mainQueueUrl }, TestContext.Current.CancellationToken);
        Assert.Empty(mainReceiveResult.Messages);
    }

    [Fact, Trait("Category", "TimeBasedTests")]
    public async Task StartMessageMoveTaskAsync_MaxNumberOfMessagesPerSecond_RespectsLimit()
    {
        await SetupQueuesAndMessage();

        // Add more messages to the source queue (DLQ)
        for (int i = 0; i < 5; i++)
        {
            await Sqs.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = _errorQueueUrl,
                MessageBody = $"Test message {i + 2}"
            }, TestContext.Current.CancellationToken);
        }

        // Start the message move task with a limit of 3 messages per second
        var startMoveResponse = await Sqs.StartMessageMoveTaskAsync(new StartMessageMoveTaskRequest
        {
            SourceArn = _errorQueueArn,
            DestinationArn = _mainQueueArn,
            MaxNumberOfMessagesPerSecond = 3
        }, TestContext.Current.CancellationToken);

        Assert.NotNull(startMoveResponse.TaskHandle);

        // Wait for 1 second (should move approximately 3 messages)
        await AdvanceTime(TimeSpan.FromSeconds(1));

        // Check that only about 3 messages were moved to the main queue
        var mainReceiveResult = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest 
        { 
            QueueUrl = _mainQueueUrl,
            MaxNumberOfMessages = 10
        }, TestContext.Current.CancellationToken);
        Assert.InRange(mainReceiveResult.Messages.Count, 2, 4); // Allow for some flexibility due to timing

        // Check that about 3 messages remain in the source queue (DLQ)
        var sourceReceiveResult = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest 
        { 
            QueueUrl = _errorQueueUrl,
            MaxNumberOfMessages = 10
        }, TestContext.Current.CancellationToken);
        Assert.InRange(sourceReceiveResult.Messages.Count, 4, 6); // Allow for some flexibility due to timing
    }

    [Fact, Trait("Category", "TimeBasedTests")]
    public async Task StartMessageMoveTaskAsync_NoDestinationArn_MovesToOriginalSource()
    {
        await SetupQueuesAndMessage();

        // Start the message move task without specifying a destination ARN
        var startMoveResponse = await Sqs.StartMessageMoveTaskAsync(new StartMessageMoveTaskRequest
        {
            SourceArn = _errorQueueArn,
            MaxNumberOfMessagesPerSecond = 10
        }, TestContext.Current.CancellationToken);

        Assert.NotNull(startMoveResponse.TaskHandle);

        // Wait for the move to complete
        await AdvanceTime(TimeSpan.FromSeconds(5));

        // Check that the message is now in the main queue (original source)
        var mainReceiveResult = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = _mainQueueUrl }, TestContext.Current.CancellationToken);
        Assert.Single(mainReceiveResult.Messages);

        // Check that the source queue (DLQ) is empty
        var sourceReceiveResult = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = _errorQueueUrl }, TestContext.Current.CancellationToken);
        Assert.Empty(sourceReceiveResult.Messages);
    }

    [Fact, Trait("Category", "TimeBasedTests")]
    public async Task CancelMessageMoveTaskAsync_ValidTaskHandle_StopsTask()
    {
        await SetupQueuesAndMessage();

        // Start the message move task
        var startMoveResponse = await Sqs.StartMessageMoveTaskAsync(new StartMessageMoveTaskRequest
        {
            SourceArn = _errorQueueArn,
            DestinationArn = _mainQueueArn,
            MaxNumberOfMessagesPerSecond = 1 // Set a low rate to ensure we can stop before completion
        }, TestContext.Current.CancellationToken);

        // Cancel the task immediately
        await Sqs.CancelMessageMoveTaskAsync(new CancelMessageMoveTaskRequest
        {
            TaskHandle = startMoveResponse.TaskHandle
        }, TestContext.Current.CancellationToken);

        // Wait a moment to ensure the task has time to stop
        await AdvanceTime(TimeSpan.FromSeconds(5));

        // Check that the message is still in the source queue (DLQ)
        var sourceReceiveResult = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = _errorQueueUrl }, TestContext.Current.CancellationToken);
        Assert.NotEmpty(sourceReceiveResult.Messages);
    }

    [Fact, Trait("Category", "TimeBasedTests")]
    public async Task StartingTwoMessageMoveTasksForTheSameQueue_Throws()
    {
        await SetupQueuesAndMessage();

        // Start two message move tasks
        var task1 = await Sqs.StartMessageMoveTaskAsync(new StartMessageMoveTaskRequest
        {
            SourceArn = _errorQueueArn,
            DestinationArn = _mainQueueArn,
            MaxNumberOfMessagesPerSecond = 10
        }, TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            var task2 = await Sqs.StartMessageMoveTaskAsync(new StartMessageMoveTaskRequest
            {
                SourceArn = _errorQueueArn,
                DestinationArn = _mainQueueArn,
                MaxNumberOfMessagesPerSecond = 5
            }, TestContext.Current.CancellationToken);
        });
    }
    
    [Fact, Trait("Category", "TimeBasedTests")]
    public async Task ListMessageMoveTasks_ReturnsAllActiveTasks()
    {
        await SetupQueuesAndMessage();

        // Create a second DLQ and main queue
        var createSecondDlqResponse = await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "second-dlq" }, TestContext.Current.CancellationToken);
        var secondDlqUrl = createSecondDlqResponse.QueueUrl;
        var secondDlqArn = await GetQueueArnFromUrl(secondDlqUrl);

        var createSecondMainQueueResponse = await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "second-main-queue" }, TestContext.Current.CancellationToken);
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
        }, TestContext.Current.CancellationToken);

        // Start two message move tasks, one for each DLQ
        var task1 = await Sqs.StartMessageMoveTaskAsync(new StartMessageMoveTaskRequest
        {
            SourceArn = _errorQueueArn,
            DestinationArn = _mainQueueArn,
            MaxNumberOfMessagesPerSecond = 10
        }, TestContext.Current.CancellationToken);

        // List the active tasks
        var listTasksResponse = await Sqs.ListMessageMoveTasksAsync(new ListMessageMoveTasksRequest
        {
            SourceArn = _errorQueueArn
        }, TestContext.Current.CancellationToken);

        var task1Info = Assert.Single(listTasksResponse.Results);

        // Verify the details of the task
        Assert.Equal(_errorQueueArn, task1Info.SourceArn);
        Assert.Equal(_mainQueueArn, task1Info.DestinationArn);
        Assert.Equal(10, task1Info.MaxNumberOfMessagesPerSecond);
    }
}

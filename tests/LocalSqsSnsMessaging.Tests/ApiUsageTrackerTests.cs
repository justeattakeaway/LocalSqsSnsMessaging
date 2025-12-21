using System.Text.Json;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using Shouldly;

namespace LocalSqsSnsMessaging.Tests;

/// <summary>
/// Tests for the API usage tracking feature.
/// </summary>
public sealed class ApiUsageTrackerTests
{
    [Test]
    public async Task SqsOperations_ShouldBeTracked()
    {
        // Arrange
        var bus = new InMemoryAwsBus { UsageTrackingEnabled = true };
        using var sqs = bus.CreateSqsClient();

        // Act
        var createResponse = await sqs.CreateQueueAsync("test-queue");
        await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = createResponse.QueueUrl,
            MessageBody = "Hello"
        });
        await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = createResponse.QueueUrl,
            MaxNumberOfMessages = 1
        });

        // Assert
        var actions = bus.UsageTracker.GetUsedActions().ToList();
        actions.ShouldContain("sqs:CreateQueue");
        actions.ShouldContain("sqs:SendMessage");
        actions.ShouldContain("sqs:ReceiveMessage");
    }

    [Test]
    public async Task SnsOperations_ShouldBeTracked()
    {
        // Arrange
        var bus = new InMemoryAwsBus { UsageTrackingEnabled = true };
        using var sns = bus.CreateSnsClient();
        using var sqs = bus.CreateSqsClient();

        // Create queue for subscription
        var queueUrl = (await sqs.CreateQueueAsync("test-queue")).QueueUrl;
        var queueArn = $"arn:aws:sqs:{bus.CurrentRegion}:{bus.CurrentAccountId}:test-queue";

        // Act
        var createResponse = await sns.CreateTopicAsync("test-topic");
        await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = createResponse.TopicArn,
            Protocol = "sqs",
            Endpoint = queueArn
        });
        await sns.PublishAsync(new PublishRequest
        {
            TopicArn = createResponse.TopicArn,
            Message = "Hello"
        });

        // Assert
        var actions = bus.UsageTracker.GetUsedActions().ToList();
        actions.ShouldContain("sns:CreateTopic");
        actions.ShouldContain("sns:Subscribe");
        actions.ShouldContain("sns:Publish");
    }

    [Test]
    public async Task GetAccessedResources_ShouldReturnQueueArns()
    {
        // Arrange
        var bus = new InMemoryAwsBus { UsageTrackingEnabled = true };
        using var sqs = bus.CreateSqsClient();

        // Act
        var queue1 = await sqs.CreateQueueAsync("queue-1");
        var queue2 = await sqs.CreateQueueAsync("queue-2");
        await sqs.SendMessageAsync(queue1.QueueUrl, "Message 1");
        await sqs.SendMessageAsync(queue2.QueueUrl, "Message 2");

        // Assert
        var resources = bus.UsageTracker.GetAccessedResources().ToList();
        resources.ShouldContain(r => r.Contains("queue-1"));
        resources.ShouldContain(r => r.Contains("queue-2"));
    }

    [Test]
    public async Task GetAccessedResourcesForService_ShouldFilterByService()
    {
        // Arrange
        var bus = new InMemoryAwsBus { UsageTrackingEnabled = true };
        using var sqs = bus.CreateSqsClient();
        using var sns = bus.CreateSnsClient();

        // Act
        await sqs.CreateQueueAsync("test-queue");
        await sns.CreateTopicAsync("test-topic");

        // Assert
        var sqsResources = bus.UsageTracker.GetAccessedResourcesForService("sqs").ToList();
        var snsResources = bus.UsageTracker.GetAccessedResourcesForService("sns").ToList();

        sqsResources.ShouldAllBe(r => r.Contains(":sqs:"));
        snsResources.ShouldAllBe(r => r.Contains(":sns:"));
    }

    [Test]
    public async Task GenerateIamPolicyJson_ShouldProduceValidJson()
    {
        // Arrange
        var bus = new InMemoryAwsBus { UsageTrackingEnabled = true };
        using var sqs = bus.CreateSqsClient();
        using var sns = bus.CreateSnsClient();

        var queueUrl = (await sqs.CreateQueueAsync("test-queue")).QueueUrl;
        await sqs.SendMessageAsync(queueUrl, "Hello");

        await sns.CreateTopicAsync("test-topic");

        // Act
        var policyJson = bus.UsageTracker.GenerateIamPolicyJson();

        // Assert - should be valid JSON
        var policy = JsonDocument.Parse(policyJson);
        policy.RootElement.GetProperty("Version").GetString().ShouldBe("2012-10-17");
        policy.RootElement.GetProperty("Statement").GetArrayLength().ShouldBeGreaterThan(0);
    }

    [Test]
    public async Task GenerateIamStatements_ShouldGroupByService()
    {
        // Arrange
        var bus = new InMemoryAwsBus { UsageTrackingEnabled = true };
        using var sqs = bus.CreateSqsClient();
        using var sns = bus.CreateSnsClient();

        var queueUrl = (await sqs.CreateQueueAsync("test-queue")).QueueUrl;
        await sqs.SendMessageAsync(queueUrl, "Hello");
        await sns.CreateTopicAsync("test-topic");

        // Act
        var statements = bus.UsageTracker.GenerateIamStatements();

        // Assert
        statements.Count.ShouldBe(2); // One for SQS, one for SNS

        var sqsStatement = statements.First(s => s.Actions.Any(a => a.StartsWith("sqs:", StringComparison.Ordinal)));
        sqsStatement.Effect.ShouldBe("Allow");
        sqsStatement.Actions.ShouldContain("sqs:CreateQueue");
        sqsStatement.Actions.ShouldContain("sqs:SendMessage");
        sqsStatement.Resources.ShouldContain(r => r.Contains("test-queue"));

        var snsStatement = statements.First(s => s.Actions.Any(a => a.StartsWith("sns:", StringComparison.Ordinal)));
        snsStatement.Effect.ShouldBe("Allow");
        snsStatement.Actions.ShouldContain("sns:CreateTopic");
        snsStatement.Resources.ShouldContain(r => r.Contains("test-topic"));
    }

    [Test]
    public async Task UsageTrackingEnabled_WhenDisabled_ShouldNotRecordOperations()
    {
        // Arrange
        var bus = new InMemoryAwsBus { UsageTrackingEnabled = false };
        using var sqs = bus.CreateSqsClient();

        // Act
        await sqs.CreateQueueAsync("test-queue");

        // Assert
        bus.UsageTracker.Operations.ShouldBeEmpty();
        bus.UsageTracker.GetUsedActions().ShouldBeEmpty();
    }

    [Test]
    public async Task UsageTrackingEnabled_CanBeToggledAtRuntime()
    {
        // Arrange
        var bus = new InMemoryAwsBus { UsageTrackingEnabled = true };
        using var sqs = bus.CreateSqsClient();

        // Act - track first operation
        await sqs.CreateQueueAsync("queue-1");

        // Disable tracking
        bus.UsageTrackingEnabled = false;
        await sqs.CreateQueueAsync("queue-2");

        // Re-enable tracking
        bus.UsageTrackingEnabled = true;
        await sqs.CreateQueueAsync("queue-3");

        // Assert
        var resources = bus.UsageTracker.GetAccessedResources().ToList();
        resources.ShouldContain(r => r.Contains("queue-1"));
        resources.ShouldNotContain(r => r.Contains("queue-2"));
        resources.ShouldContain(r => r.Contains("queue-3"));
    }

    [Test]
    public async Task Clear_ShouldRemoveAllRecordedOperations()
    {
        // Arrange
        var bus = new InMemoryAwsBus { UsageTrackingEnabled = true };
        using var sqs = bus.CreateSqsClient();
        await sqs.CreateQueueAsync("test-queue");

        // Verify operations were recorded
        bus.UsageTracker.Operations.Count.ShouldBeGreaterThan(0);

        // Act
        bus.UsageTracker.Clear();

        // Assert
        bus.UsageTracker.Operations.ShouldBeEmpty();
        bus.UsageTracker.GetUsedActions().ShouldBeEmpty();
        bus.UsageTracker.GetAccessedResources().ShouldBeEmpty();
    }

    [Test]
    public async Task Operations_ShouldIncludeTimestamp()
    {
        // Arrange
        var bus = new InMemoryAwsBus { UsageTrackingEnabled = true };
        using var sqs = bus.CreateSqsClient();

        var beforeTime = bus.TimeProvider.GetUtcNow();

        // Act
        await sqs.CreateQueueAsync("test-queue");

        var afterTime = bus.TimeProvider.GetUtcNow();

        // Assert
        var operation = bus.UsageTracker.Operations[0];
        operation.Timestamp.ShouldBeGreaterThanOrEqualTo(beforeTime);
        operation.Timestamp.ShouldBeLessThanOrEqualTo(afterTime);
    }

    [Test]
    public async Task Operations_ShouldIndicateSuccess()
    {
        // Arrange
        var bus = new InMemoryAwsBus { UsageTrackingEnabled = true };
        using var sqs = bus.CreateSqsClient();

        // Act
        await sqs.CreateQueueAsync("test-queue");

        // Assert
        var operation = bus.UsageTracker.Operations[0];
        operation.Success.ShouldBeTrue();
    }

    [Test]
    public async Task GetUsedActionsForService_ShouldFilterCorrectly()
    {
        // Arrange
        var bus = new InMemoryAwsBus { UsageTrackingEnabled = true };
        using var sqs = bus.CreateSqsClient();
        using var sns = bus.CreateSnsClient();

        await sqs.CreateQueueAsync("test-queue");
        await sns.CreateTopicAsync("test-topic");

        // Act
        var sqsActions = bus.UsageTracker.GetUsedActionsForService("sqs").ToList();
        var snsActions = bus.UsageTracker.GetUsedActionsForService("sns").ToList();

        // Assert
        sqsActions.ShouldAllBe(a => a.StartsWith("sqs:"));
        snsActions.ShouldAllBe(a => a.StartsWith("sns:"));
    }

    [Test]
    public async Task ListOperations_ShouldNotRecordResourceArn()
    {
        // Arrange
        var bus = new InMemoryAwsBus { UsageTrackingEnabled = true };
        using var sqs = bus.CreateSqsClient();

        // Act
        await sqs.ListQueuesAsync(new ListQueuesRequest());

        // Assert
        var operation = bus.UsageTracker.Operations.First(o => o.Action == "ListQueues");
        operation.ResourceArn.ShouldBeNull();
    }
}

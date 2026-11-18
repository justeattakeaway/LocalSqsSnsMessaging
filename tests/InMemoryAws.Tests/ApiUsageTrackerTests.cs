using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using Shouldly;

namespace InMemoryAws.Tests;

/// <summary>
/// Tests for the API usage tracking feature.
/// </summary>
public sealed class ApiUsageTrackerTests
{
    [Test]
    public async Task SqsOperations_ShouldBeTracked()
    {
        // Arrange
        var cloud = new InMemoryAwsCloud { UsageTrackingEnabled = true };
        using var sqs = cloud.CreateSqsClient();

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
        var actions = cloud.UsageTracker.GetUsedActions().OrderBy(a => a).ToList();
        actions.ShouldBe(["sqs:CreateQueue", "sqs:ReceiveMessage", "sqs:SendMessage"]);
    }

    [Test]
    public async Task SnsOperations_ShouldBeTracked()
    {
        // Arrange
        var cloud = new InMemoryAwsCloud { UsageTrackingEnabled = true };
        using var sns = cloud.CreateSnsClient();
        using var sqs = cloud.CreateSqsClient();

        // Create queue for subscription
        var queueUrl = (await sqs.CreateQueueAsync("test-queue")).QueueUrl;
        var queueArn = $"arn:aws:sqs:{cloud.CurrentRegion}:{cloud.CurrentAccountId}:test-queue";

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
        var actions = cloud.UsageTracker.GetUsedActions().OrderBy(a => a).ToList();
        actions.ShouldBe(["sns:CreateTopic", "sns:Publish", "sns:Subscribe", "sqs:CreateQueue"]);
    }

    [Test]
    public async Task GetAccessedResources_ShouldReturnQueueArns()
    {
        // Arrange
        var cloud = new InMemoryAwsCloud { UsageTrackingEnabled = true };
        using var sqs = cloud.CreateSqsClient();

        // Act
        var queue1 = await sqs.CreateQueueAsync("queue-1");
        var queue2 = await sqs.CreateQueueAsync("queue-2");
        await sqs.SendMessageAsync(queue1.QueueUrl, "Message 1");
        await sqs.SendMessageAsync(queue2.QueueUrl, "Message 2");

        // Assert
        var resources = cloud.UsageTracker.GetAccessedResources().OrderBy(r => r).ToList();
        resources.ShouldBe([
            $"arn:aws:sqs:{cloud.CurrentRegion}:{cloud.CurrentAccountId}:queue-1",
            $"arn:aws:sqs:{cloud.CurrentRegion}:{cloud.CurrentAccountId}:queue-2"
        ]);
    }

    [Test]
    public async Task GetAccessedResourcesForService_ShouldFilterByService()
    {
        // Arrange
        var cloud = new InMemoryAwsCloud { UsageTrackingEnabled = true };
        using var sqs = cloud.CreateSqsClient();
        using var sns = cloud.CreateSnsClient();

        // Act
        await sqs.CreateQueueAsync("test-queue");
        await sns.CreateTopicAsync("test-topic");

        // Assert
        var sqsResources = cloud.UsageTracker.GetAccessedResourcesForService("sqs").ToList();
        var snsResources = cloud.UsageTracker.GetAccessedResourcesForService("sns").ToList();

        sqsResources.ShouldBe([$"arn:aws:sqs:{cloud.CurrentRegion}:{cloud.CurrentAccountId}:test-queue"]);
        snsResources.ShouldBe([$"arn:aws:sns:{cloud.CurrentRegion}:{cloud.CurrentAccountId}:test-topic"]);
    }

    [Test]
    public async Task GenerateIamPolicyJson_ShouldProduceValidJson()
    {
        // Arrange
        var cloud = new InMemoryAwsCloud { UsageTrackingEnabled = true };
        using var sqs = cloud.CreateSqsClient();
        using var sns = cloud.CreateSnsClient();

        var queueUrl = (await sqs.CreateQueueAsync("test-queue")).QueueUrl;
        await sqs.SendMessageAsync(queueUrl, "Hello");

        await sns.CreateTopicAsync("test-topic");

        // Act
        var policyJson = cloud.UsageTracker.GenerateIamPolicyJson();

        // Assert
        policyJson.ShouldBe(
            """
            {
              "Version": "2012-10-17",
              "Statement": [
                {
                  "Effect": "Allow",
                  "Action": [
                    "sns:CreateTopic"
                  ],
                  "Resource": [
                    "arn:aws:sns:us-east-1:000000000000:test-topic"
                  ]
                },
                {
                  "Effect": "Allow",
                  "Action": [
                    "sqs:CreateQueue",
                    "sqs:SendMessage"
                  ],
                  "Resource": [
                    "arn:aws:sqs:us-east-1:000000000000:test-queue"
                  ]
                }
              ]
            }
            """);
    }

    [Test]
    public async Task GenerateIamStatements_ShouldGroupByService()
    {
        // Arrange
        var cloud = new InMemoryAwsCloud { UsageTrackingEnabled = true };
        using var sqs = cloud.CreateSqsClient();
        using var sns = cloud.CreateSnsClient();

        var queueUrl = (await sqs.CreateQueueAsync("test-queue")).QueueUrl;
        await sqs.SendMessageAsync(queueUrl, "Hello");
        await sns.CreateTopicAsync("test-topic");

        // Act
        var statements = cloud.UsageTracker.GenerateIamStatements();

        // Assert
        statements.Count.ShouldBe(2);

        var sqsStatement = statements.First(s => s.Actions.Any(a => a.StartsWith("sqs:", StringComparison.Ordinal)));
        sqsStatement.Effect.ShouldBe("Allow");
        sqsStatement.Actions.OrderBy(a => a).ShouldBe(["sqs:CreateQueue", "sqs:SendMessage"]);
        sqsStatement.Resources.ShouldBe([$"arn:aws:sqs:{cloud.CurrentRegion}:{cloud.CurrentAccountId}:test-queue"]);

        var snsStatement = statements.First(s => s.Actions.Any(a => a.StartsWith("sns:", StringComparison.Ordinal)));
        snsStatement.Effect.ShouldBe("Allow");
        snsStatement.Actions.ShouldBe(["sns:CreateTopic"]);
        snsStatement.Resources.ShouldBe([$"arn:aws:sns:{cloud.CurrentRegion}:{cloud.CurrentAccountId}:test-topic"]);
    }

    [Test]
    public async Task UsageTrackingEnabled_WhenDisabled_ShouldNotRecordOperations()
    {
        // Arrange
        var cloud = new InMemoryAwsCloud { UsageTrackingEnabled = false };
        using var sqs = cloud.CreateSqsClient();

        // Act
        await sqs.CreateQueueAsync("test-queue");

        // Assert
        cloud.UsageTracker.Operations.ShouldBeEmpty();
        cloud.UsageTracker.GetUsedActions().ShouldBeEmpty();
    }

    [Test]
    public async Task UsageTrackingEnabled_CanBeToggledAtRuntime()
    {
        // Arrange
        var cloud = new InMemoryAwsCloud { UsageTrackingEnabled = true };
        using var sqs = cloud.CreateSqsClient();

        // Act - track first operation
        await sqs.CreateQueueAsync("queue-1");

        // Disable tracking
        cloud.UsageTrackingEnabled = false;
        await sqs.CreateQueueAsync("queue-2");

        // Re-enable tracking
        cloud.UsageTrackingEnabled = true;
        await sqs.CreateQueueAsync("queue-3");

        // Assert - verify that only queue-1 and queue-3 were tracked (not queue-2)
        var resources = cloud.UsageTracker.GetAccessedResources().OrderBy(r => r).ToList();
        resources.ShouldBe([
            $"arn:aws:sqs:{cloud.CurrentRegion}:{cloud.CurrentAccountId}:queue-1",
            $"arn:aws:sqs:{cloud.CurrentRegion}:{cloud.CurrentAccountId}:queue-3"
        ]);
    }

    [Test]
    public async Task Clear_ShouldRemoveAllRecordedOperations()
    {
        // Arrange
        var cloud = new InMemoryAwsCloud { UsageTrackingEnabled = true };
        using var sqs = cloud.CreateSqsClient();
        await sqs.CreateQueueAsync("test-queue");

        // Verify operations were recorded
        cloud.UsageTracker.Operations.Count.ShouldBeGreaterThan(0);

        // Act
        cloud.UsageTracker.Clear();

        // Assert
        cloud.UsageTracker.Operations.ShouldBeEmpty();
        cloud.UsageTracker.GetUsedActions().ShouldBeEmpty();
        cloud.UsageTracker.GetAccessedResources().ShouldBeEmpty();
    }

    [Test]
    public async Task Operations_ShouldIncludeTimestamp()
    {
        // Arrange
        var cloud = new InMemoryAwsCloud { UsageTrackingEnabled = true };
        using var sqs = cloud.CreateSqsClient();

        var beforeTime = cloud.TimeProvider.GetUtcNow();

        // Act
        await sqs.CreateQueueAsync("test-queue");

        var afterTime = cloud.TimeProvider.GetUtcNow();

        // Assert
        var operation = cloud.UsageTracker.Operations[0];
        operation.Timestamp.ShouldBeGreaterThanOrEqualTo(beforeTime);
        operation.Timestamp.ShouldBeLessThanOrEqualTo(afterTime);
    }

    [Test]
    public async Task Operations_ShouldIndicateSuccess()
    {
        // Arrange
        var cloud = new InMemoryAwsCloud { UsageTrackingEnabled = true };
        using var sqs = cloud.CreateSqsClient();

        // Act
        await sqs.CreateQueueAsync("test-queue");

        // Assert
        var operation = cloud.UsageTracker.Operations[0];
        operation.Action.ShouldBe("CreateQueue");
        operation.Service.ShouldBe("sqs");
        operation.Success.ShouldBeTrue();
    }

    [Test]
    public async Task GetUsedActionsForService_ShouldFilterCorrectly()
    {
        // Arrange
        var cloud = new InMemoryAwsCloud { UsageTrackingEnabled = true };
        using var sqs = cloud.CreateSqsClient();
        using var sns = cloud.CreateSnsClient();

        await sqs.CreateQueueAsync("test-queue");
        await sns.CreateTopicAsync("test-topic");

        // Act
        var sqsActions = cloud.UsageTracker.GetUsedActionsForService("sqs").ToList();
        var snsActions = cloud.UsageTracker.GetUsedActionsForService("sns").ToList();

        // Assert
        sqsActions.ShouldBe(["sqs:CreateQueue"]);
        snsActions.ShouldBe(["sns:CreateTopic"]);
    }

    [Test]
    public async Task ListOperations_ShouldNotRecordResourceArn()
    {
        // Arrange
        var cloud = new InMemoryAwsCloud { UsageTrackingEnabled = true };
        using var sqs = cloud.CreateSqsClient();

        // Act
        await sqs.ListQueuesAsync(new ListQueuesRequest());

        // Assert
        var operation = cloud.UsageTracker.Operations.First(o => o.Action == "ListQueues");
        operation.Action.ShouldBe("ListQueues");
        operation.Service.ShouldBe("sqs");
        operation.ResourceArn.ShouldBeNull();
        operation.Success.ShouldBeTrue();
    }
}

using System.Text;
using LocalSqsSnsMessaging.Server;
using LocalSqsSnsMessaging.Sns.Model;
using LocalSqsSnsMessaging.Sqs.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Shouldly;

namespace LocalSqsSnsMessaging.Tests.Server;

/// <summary>
/// The dashboard's Publish button. A FIFO topic refuses a publish without a message group (and,
/// unless it deduplicates by content, a deduplication id), so the dashboard supplies both.
/// </summary>
public class DashboardPublishTests
{
    private const string AccountId = "000000000000";

    [Test]
    public async Task PublishToTopic_DeliversToAFifoTopicsQueue()
    {
        var registry = new BusRegistry(AccountId, "us-east-1", new Uri("http://localhost:4566"));
        var sns = new InternalSnsClient(registry.DefaultBus);
        var sqs = new InternalSqsClient(registry.DefaultBus);
        var topicArn = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = "dashboard-topic.fifo",
            Attributes = new() { ["FifoTopic"] = "true" },
        })).TopicArn!;
        var queueUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = "dashboard-queue.fifo",
            Attributes = new() { ["FifoQueue"] = "true" },
        })).QueueUrl!;
        await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "sqs",
            Endpoint = $"arn:aws:sqs:us-east-1:{AccountId}:dashboard-queue.fifo",
            Attributes = new() { ["RawMessageDelivery"] = "true" },
        });

        // Two identical publishes are two distinct messages, not a duplicate.
        for (var i = 0; i < 2; i++)
        {
            var context = new DefaultHttpContext();
            context.Request.ContentType = "application/json";
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("""{"message":"from the dashboard"}"""));

            var result = await DashboardApi.PublishToTopic(registry, AccountId, "dashboard-topic.fifo", context);
            result.ShouldBeOfType<JsonHttpResult<DashboardApi.PublishTopicResponse>>();
        }

        var received = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 10,
        });
        received.Messages!.Select(m => m.Body).ShouldBe(["from the dashboard", "from the dashboard"]);
    }
}

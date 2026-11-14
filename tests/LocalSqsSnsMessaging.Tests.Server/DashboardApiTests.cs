using LocalSqsSnsMessaging.Server;
using LocalSqsSnsMessaging.Sqs.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Shouldly;

namespace LocalSqsSnsMessaging.Tests.Server;

/// <summary>
/// Covers the dashboard's read and delete of the messages waiting on a queue. Both used to
/// reach into the private <c>_items</c> field of the channel backing the queue by reflection,
/// because a channel has no way to look at its contents without consuming them; they now go
/// through <see cref="SqsMessageStore"/>, which supports both directly.
/// </summary>
public class DashboardApiTests
{
    private const string AccountId = "000000000000";

    private static (BusRegistry Registry, InternalSqsClient Sqs, string QueueUrl) CreateQueue(string queueName)
    {
        var registry = new BusRegistry(AccountId, "us-east-1", new Uri("http://localhost:4566"));
        var sqs = new InternalSqsClient(registry.DefaultBus);
        var queueUrl = sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = queueName })
            .GetAwaiter().GetResult().QueueUrl!;
        return (registry, sqs, queueUrl);
    }

    private static async Task<string> SendAsync(InternalSqsClient sqs, string queueUrl, string body)
    {
        var response = await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = body
        });
        return response.MessageId!;
    }

    [Test]
    public async Task GetQueueMessages_ReturnsWaitingMessagesWithoutConsumingThem()
    {
        var (registry, sqs, queueUrl) = CreateQueue("dashboard-queue");
        await SendAsync(sqs, queueUrl, "first");
        await SendAsync(sqs, queueUrl, "second");

        var result = DashboardApi.GetQueueMessages(registry, AccountId, null, "dashboard-queue")
            .ShouldBeOfType<JsonHttpResult<DashboardApi.QueueMessages>>();

        result.Value!.PendingMessages.Select(m => m.Body).ShouldBe(["first", "second"]);
        result.Value.InFlightMessages.ShouldBeEmpty();

        // Looking must not consume: the messages are still there to be received.
        var received = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 10
        });
        received.Messages!.Select(m => m.Body).ShouldBe(["first", "second"]);

        // ...and once received they move from waiting to in flight.
        var afterReceive = DashboardApi.GetQueueMessages(registry, AccountId, null, "dashboard-queue")
            .ShouldBeOfType<JsonHttpResult<DashboardApi.QueueMessages>>();
        afterReceive.Value!.PendingMessages.ShouldBeEmpty();
        afterReceive.Value.InFlightMessages.Select(m => m.Body).ShouldBe(["first", "second"], ignoreOrder: true);
    }

    [Test]
    public async Task DeleteMessage_RemovesOnlyThatMessageAndLeavesTheRestInOrder()
    {
        var (registry, sqs, queueUrl) = CreateQueue("dashboard-delete-queue");
        await SendAsync(sqs, queueUrl, "first");
        var second = await SendAsync(sqs, queueUrl, "second");
        await SendAsync(sqs, queueUrl, "third");

        DashboardApi.DeleteMessage(registry, AccountId, null, "dashboard-delete-queue", second)
            .ShouldBeOfType<NoContent>();

        var received = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 10
        });
        received.Messages!.Select(m => m.Body).ShouldBe(["first", "third"]);
    }

    [Test]
    public async Task DeleteMessage_UnknownMessageId_ReturnsNotFound()
    {
        var (registry, sqs, queueUrl) = CreateQueue("dashboard-missing-queue");
        await SendAsync(sqs, queueUrl, "first");

        DashboardApi.DeleteMessage(registry, AccountId, null, "dashboard-missing-queue", "no-such-message-id")
            .ShouldBeOfType<NotFound<string>>();

        var received = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 10
        });
        received.Messages!.ShouldHaveSingleItem().Body.ShouldBe("first");
    }

    [Test]
    public async Task GetState_ListsTheViewedRegionEvenWhenItIsEmpty()
    {
        var registry = new BusRegistry(AccountId, "us-east-1", new Uri("http://localhost:4566"));
        var euSqs = new InternalSqsClient(registry.DefaultBus, "eu-west-1");
        var queueUrl = (await euSqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "dashboard-eu-queue" })).QueueUrl!;
        await euSqs.DeleteQueueAsync(new DeleteQueueRequest { QueueUrl = queueUrl });

        // eu-west-1 holds nothing now, but the dashboard is still viewing it: the region selector
        // must keep offering it, or the viewer can no longer switch away from it.
        var state = DashboardApi.GetState(registry, AccountId, "eu-west-1")
            .ShouldBeOfType<JsonHttpResult<DashboardApi.BusState>>().Value!;
        state.CurrentRegion.ShouldBe("eu-west-1");
        state.Regions.ShouldBe(["eu-west-1", "us-east-1"]);

        var home = DashboardApi.GetState(registry, AccountId, null)
            .ShouldBeOfType<JsonHttpResult<DashboardApi.BusState>>().Value!;
        home.Regions.ShouldBe(["us-east-1"]);
    }
}

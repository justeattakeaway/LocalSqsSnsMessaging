using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Shouldly;
using SnsMessageAttributeValue = Amazon.SimpleNotificationService.Model.MessageAttributeValue;
using TopicPublishRequest = LocalSqsSnsMessaging.Sns.Model.PublishRequest;

namespace LocalSqsSnsMessaging.Tests.Sns;

/// <summary>
/// A topic keeps its subscription set and its fan-out in step: subscriptions made, confirmed and
/// removed concurrently all land, a publish sees a subscription whole or not at all, and a FIFO
/// replay is delivered before the live messages that follow it.
/// </summary>
public sealed class SnsTopicFanOutTests : IDisposable
{
    private readonly InMemoryAwsBus _bus = new();
    private readonly AmazonSimpleNotificationServiceClient _sns;
    private readonly AmazonSQSClient _sqs;

    public SnsTopicFanOutTests()
    {
        _sns = _bus.CreateSnsClient();
        _sqs = _bus.CreateSqsClient();
    }

    public void Dispose()
    {
        _sns.Dispose();
        _sqs.Dispose();
    }

    [Test]
    [Timeout(60_000)]
    public async Task ConcurrentSubscribesAndUnsubscribes_AllLandInTheFanOut(CancellationToken cancellationToken)
    {
        var topicArn = (await _sns.CreateTopicAsync("concurrent-membership", cancellationToken)).TopicArn;
        var leaving = new List<(string Url, string SubscriptionArn)>();
        for (var i = 0; i < 16; i++)
        {
            var (url, arn) = await CreateQueueAsync($"leaving-{i}");
            leaving.Add((url, (await _sns.SubscribeAsync(topicArn, "sqs", arn, cancellationToken)).SubscriptionArn));
        }

        var joining = new List<(string Url, string Arn)>();
        for (var i = 0; i < 32; i++)
        {
            joining.Add(await CreateQueueAsync($"joining-{i}"));
        }

        // Each change replaces the topic's fan-out: one that raced another and lost would drop a
        // subscriber, or bring back one that had left.
        var changes = joining.Select<(string Url, string Arn), Action>(q => () => _sns.SubscribeAsync(topicArn, "sqs", q.Arn).GetAwaiter().GetResult())
            .Concat(leaving.Select<(string Url, string SubscriptionArn), Action>(q => () => _sns.UnsubscribeAsync(q.SubscriptionArn).GetAwaiter().GetResult()))
            .ToList();
        await RunTogether(changes);

        await _sns.PublishAsync(topicArn, "after the changes", cancellationToken);

        foreach (var (url, _) in joining)
        {
            (await ReceiveAllAsync(url)).Count.ShouldBe(1, url);
        }

        foreach (var (url, _) in leaving)
        {
            (await ReceiveAllAsync(url)).ShouldBeEmpty(url);
        }

        var attributes = (await _sns.GetTopicAttributesAsync(topicArn, cancellationToken)).Attributes;
        attributes["SubscriptionsConfirmed"].ShouldBe("32");
        attributes["SubscriptionsDeleted"].ShouldBe("16");
        var listed = (await _sns.ListSubscriptionsByTopicAsync(topicArn, cancellationToken)).Subscriptions;
        listed.Select(s => s.Endpoint).ShouldBe(joining.Select(q => q.Arn), ignoreOrder: true);
    }

    [Test]
    [Timeout(60_000)]
    public async Task PublishesDuringSubscribe_NeverSeeAHalfAddedSubscription(CancellationToken cancellationToken)
    {
        var topicArn = (await _sns.CreateTopicAsync("half-added", cancellationToken)).TopicArn;
        var queues = new List<(string Url, string Arn)>();
        for (var i = 0; i < 24; i++)
        {
            queues.Add(await CreateQueueAsync($"half-added-{i}"));
        }

        // Every subscription is raw and takes only red: a publish that reached one before its
        // attributes were applied would deliver an envelope, or a blue message.
        using var subscribed = new CancellationTokenSource();
        var publishing = Task.Factory.StartNew(() =>
        {
            for (var i = 0; !subscribed.IsCancellationRequested; i++)
            {
                var colour = i % 2 == 0 ? "red" : "blue";
                _sns.PublishAsync(Colour(topicArn, colour, $"{colour}-{i}")).GetAwaiter().GetResult();
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        await RunTogether(queues.Select<(string Url, string Arn), Action>(q => () => _sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "sqs",
            Endpoint = q.Arn,
            Attributes = new()
            {
                ["RawMessageDelivery"] = "true",
                ["FilterPolicy"] = """{"colour": ["red"]}""",
            },
        }).GetAwaiter().GetResult()).ToList());
        await subscribed.CancelAsync();
        await publishing.WaitAsync(cancellationToken);
        await _sns.PublishAsync(Colour(topicArn, "red", "red-final"), cancellationToken);

        foreach (var (url, _) in queues)
        {
            var bodies = (await ReceiveAllAsync(url)).Select(m => m.Body).ToList();
            bodies.ShouldAllBe(body => body.StartsWith("red-", StringComparison.Ordinal));
            bodies.ShouldContain("red-final");
        }
    }

    [Test]
    [Timeout(60_000)]
    public async Task ReplayOnSubscribe_IsDeliveredBeforeTheLiveMessagesThatFollowIt(CancellationToken cancellationToken)
    {
        var topicArn = await CreateArchivingFifoTopicAsync("replay-during-publishes.fifo");
        var beginning = (await _sns.GetTopicAttributesAsync(topicArn, cancellationToken)).Attributes["BeginningArchiveTime"];
        for (var i = 0; i < 20; i++)
        {
            await _sns.PublishAsync(Fifo(topicArn, $"m{i}"), cancellationToken);
        }

        var queues = new List<(string Url, string Arn)>();
        for (var i = 0; i < 5; i++)
        {
            queues.Add(await CreateQueueAsync($"replay-during-publishes-{i}.fifo", fifo: true));
        }

        // Each subscription joins while publishes are under way, so a live message can be ready to
        // deliver at the moment it joins the fan-out. The publishes go to the topic itself: through
        // SNS, validating a publish waits for the subscribe, which hides most of the race.
        _bus.TryGetTopicByArn(topicArn, out var topic).ShouldBeTrue();
        using var subscribed = new CancellationTokenSource();
        var published = 20;
        var publishing = Task.Factory.StartNew(() =>
        {
            while (!subscribed.IsCancellationRequested)
            {
                topic!.Publish(new TopicPublishRequest { TopicArn = topicArn, Message = $"m{published}", MessageGroupId = "g" });
                published++;
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        var subscriptionArns = new List<string>();
        foreach (var (_, arn) in queues)
        {
            subscriptionArns.Add((await _sns.SubscribeAsync(new SubscribeRequest
            {
                TopicArn = topicArn,
                Protocol = "sqs",
                Endpoint = arn,
                Attributes = new() { ["ReplayPolicy"] = ReplayFrom(beginning) },
            }, cancellationToken)).SubscriptionArn);
        }

        await subscribed.CancelAsync();
        await publishing.WaitAsync(cancellationToken);

        // The replay covers everything published before the subscription joined the fan-out, and
        // the fan-out everything after it: together, every message once, in publish order.
        var expected = Enumerable.Range(0, published).Select(i => $"m{i}").ToList();
        foreach (var (url, _) in queues)
        {
            (await ReceiveAllAsync(url)).Select(NotificationMessage).ShouldBe(expected, url);
        }

        foreach (var subscriptionArn in subscriptionArns)
        {
            (await _sns.GetSubscriptionAttributesAsync(subscriptionArn, cancellationToken)).Attributes["ReplayStatus"].ShouldBe("Completed");
        }
    }

    [Test]
    public async Task ReplayPolicySetLater_ReplaysOnceAndOtherChangesDoNotReplayAgain(CancellationToken cancellationToken)
    {
        var topicArn = await CreateArchivingFifoTopicAsync("replay-later.fifo");
        var beginning = (await _sns.GetTopicAttributesAsync(topicArn, cancellationToken)).Attributes["BeginningArchiveTime"];
        for (var i = 0; i < 3; i++)
        {
            await _sns.PublishAsync(Fifo(topicArn, $"m{i}"), cancellationToken);
        }

        var (url, arn) = await CreateQueueAsync("replay-later.fifo", fifo: true);
        var subscriptionArn = (await _sns.SubscribeAsync(topicArn, "sqs", arn, cancellationToken)).SubscriptionArn;
        (await ReceiveAllAsync(url)).ShouldBeEmpty();

        await _sns.SetSubscriptionAttributesAsync(subscriptionArn, "ReplayPolicy", ReplayFrom(beginning), cancellationToken);
        (await ReceiveAllAsync(url)).Select(NotificationMessage).ShouldBe(["m0", "m1", "m2"]);

        await _sns.SetSubscriptionAttributesAsync(subscriptionArn, "RawMessageDelivery", "true", cancellationToken);
        (await ReceiveAllAsync(url)).ShouldBeEmpty();
    }

    [Test]
    public async Task UnsubscribedOrDeletedTopicsSubscription_IsNotFound(CancellationToken cancellationToken)
    {
        var topicArn = (await _sns.CreateTopicAsync("gone", cancellationToken)).TopicArn;
        var (url, arn) = await CreateQueueAsync("gone-queue");
        var subscriptionArn = (await _sns.SubscribeAsync(topicArn, "sqs", arn, cancellationToken)).SubscriptionArn;

        await _sns.DeleteTopicAsync(topicArn, cancellationToken);
        await Should.ThrowAsync<NotFoundException>(() =>
            _sns.SetSubscriptionAttributesAsync(subscriptionArn, "RawMessageDelivery", "true", cancellationToken));

        // A topic recreated under the same ARN starts with no subscriptions, and the old one's
        // Unsubscribe leaves it alone.
        topicArn = (await _sns.CreateTopicAsync("gone", cancellationToken)).TopicArn;
        await _sns.UnsubscribeAsync(subscriptionArn, cancellationToken);
        await _sns.PublishAsync(topicArn, "to nobody", cancellationToken);
        (await ReceiveAllAsync(url)).ShouldBeEmpty();
        (await _sns.GetTopicAttributesAsync(topicArn, cancellationToken)).Attributes["SubscriptionsDeleted"].ShouldBe("0");
    }

    /// <summary>
    /// Runs <paramref name="actions"/> on dedicated threads released together: an SDK call completes
    /// synchronously against the in-memory bus, so awaiting async calls together would run them in turn.
    /// </summary>
    private static async Task RunTogether(List<Action> actions)
    {
        using var barrier = new Barrier(actions.Count);
        await Task.WhenAll(actions.Select(action => Task.Factory.StartNew(
            () =>
            {
                barrier.SignalAndWait();
                action();
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default)));
    }

    private async Task<string> CreateArchivingFifoTopicAsync(string name) =>
        (await _sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = name,
            Attributes = new()
            {
                ["FifoTopic"] = "true",
                ["ContentBasedDeduplication"] = "true",
                ["ArchivePolicy"] = """{"MessageRetentionPeriod": "1"}""",
            },
        })).TopicArn;

    private static string ReplayFrom(string beginning) =>
        new JsonObject { ["PointType"] = "Timestamp", ["StartingPoint"] = beginning }.ToJsonString();

    private static PublishRequest Fifo(string topicArn, string message) =>
        new() { TopicArn = topicArn, Message = message, MessageGroupId = "g" };

    private static PublishRequest Colour(string topicArn, string colour, string message) => new()
    {
        TopicArn = topicArn,
        Message = message,
        MessageAttributes = new() { ["colour"] = new SnsMessageAttributeValue { DataType = "String", StringValue = colour } },
    };

    private static string NotificationMessage(Message message) =>
        JsonDocument.Parse(message.Body).RootElement.GetProperty("Message").GetString()!;

    private async Task<(string Url, string Arn)> CreateQueueAsync(string name, bool fifo = false)
    {
        var url = (await _sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = name,
            Attributes = fifo ? new() { ["FifoQueue"] = "true" } : null,
        })).QueueUrl;
        var arn = (await _sqs.GetQueueAttributesAsync(url, ["QueueArn"])).Attributes["QueueArn"];
        return (url, arn);
    }

    /// <summary>Every message on the queue, in the order the queue hands them out, deleting each.</summary>
    private async Task<List<Message>> ReceiveAllAsync(string url)
    {
        var received = new List<Message>();
        while (true)
        {
            var batch = (await _sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10 })).Messages ?? [];
            if (batch.Count == 0)
            {
                return received;
            }

            received.AddRange(batch);
            await _sqs.DeleteMessageBatchAsync(url, batch.Select((m, i) => new DeleteMessageBatchRequestEntry($"{i}", m.ReceiptHandle)).ToList());
        }
    }
}

using System.Globalization;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using EventBridgeTarget = Amazon.EventBridge.Model.Target;

namespace InMemoryAws.Tests;

/// <summary>
/// What a queue does with a message an AWS service delivers (SNS for a subscription, EventBridge
/// for a rule's target) as against one a caller sends, and redrive to a dead-letter queue that has
/// been deleted and recreated.
/// </summary>
public sealed class SqsServiceDeliveryTests : IDisposable
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly InMemoryAwsCloud _cloud;
    private readonly AmazonSQSClient _sqs;
    private readonly AmazonSimpleNotificationServiceClient _sns;
    private readonly AmazonEventBridgeClient _eb;

    public SqsServiceDeliveryTests()
    {
        _cloud = new InMemoryAwsCloud { TimeProvider = _time };
        _sqs = _cloud.CreateSqsClient();
        _sns = _cloud.CreateSnsClient();
        _eb = _cloud.CreateEventBridgeClient();
    }

    public void Dispose()
    {
        _sqs.Dispose();
        _sns.Dispose();
        _eb.Dispose();
    }

    private async Task<(string Url, string Arn)> QueueAsync(string name, Dictionary<string, string>? attributes = null)
    {
        var url = (await _sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Attributes = attributes ?? [] })).QueueUrl;
        var arn = (await _sqs.GetQueueAttributesAsync(url, ["QueueArn"])).Attributes["QueueArn"];
        return (url, arn);
    }

    private async Task<List<Amazon.SQS.Model.Message>> ReceiveAllAsync(string url) =>
        (await _sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = url,
            MaxNumberOfMessages = 10,
            MessageSystemAttributeNames = ["All"],
        })).Messages ?? [];

    private string Now() => _time.GetUtcNow().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

    private static readonly Dictionary<string, string> OneMinuteRetention = new() { ["MessageRetentionPeriod"] = "60" };

    [Test]
    public async Task SnsDeliveryToAStandardQueue_IsStampedWhenDelivered_AndExpiresAfterTheQueuesRetention()
    {
        var queue = await QueueAsync("sns-retention", OneMinuteRetention);
        var topicArn = (await _sns.CreateTopicAsync("retention")).TopicArn;
        await _sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "sqs",
            Endpoint = queue.Arn,
            Attributes = new() { ["RawMessageDelivery"] = "true" },
        });

        await _sns.PublishAsync(topicArn, "old");
        _time.Advance(TimeSpan.FromSeconds(30));
        await _sns.PublishAsync(topicArn, "new");
        var sentNew = Now();
        _time.Advance(TimeSpan.FromSeconds(31));

        var received = (await ReceiveAllAsync(queue.Url)).ShouldHaveSingleItem();
        received.Body.ShouldBe("new");
        received.Attributes["SentTimestamp"].ShouldBe(sentNew);
    }

    [Test]
    public async Task EventBridgeDeliveryToAStandardQueue_IsStampedWhenDelivered_AndExpiresAfterTheQueuesRetention()
    {
        var queue = await QueueAsync("eb-retention", OneMinuteRetention);
        await _eb.PutRuleAsync(new PutRuleRequest { Name = "r", EventPattern = """{"source":["s"]}""" });
        await _eb.PutTargetsAsync(new PutTargetsRequest { Rule = "r", Targets = [new EventBridgeTarget { Id = "q", Arn = queue.Arn, Input = "\"x\"" }] });

        await PutEventAsync();
        _time.Advance(TimeSpan.FromSeconds(30));
        await PutEventAsync();
        var sentNew = Now();
        _time.Advance(TimeSpan.FromSeconds(31));

        var received = (await ReceiveAllAsync(queue.Url)).ShouldHaveSingleItem();
        received.Attributes["SentTimestamp"].ShouldBe(sentNew);
    }

    [Test]
    public async Task EventBridgeDeliveryToAFifoQueue_IsStampedWhenDelivered()
    {
        var queue = await QueueAsync("eb-stamp.fifo", new() { ["FifoQueue"] = "true" });
        await _eb.PutRuleAsync(new PutRuleRequest { Name = "r", EventPattern = """{"source":["s"]}""" });
        await _eb.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = "r",
            Targets = [new EventBridgeTarget { Id = "q", Arn = queue.Arn, SqsParameters = new SqsParameters { MessageGroupId = "g" } }],
        });

        await PutEventAsync();

        (await ReceiveAllAsync(queue.Url)).ShouldHaveSingleItem().Attributes["SentTimestamp"].ShouldBe(Now());
    }

    private async Task PutEventAsync() =>
        await _eb.PutEventsAsync(new PutEventsRequest
        {
            Entries = [new PutEventsRequestEntry { Source = "s", DetailType = "t", Detail = "{}" }],
        });

    [Test]
    public async Task Redrive_ToADeadLetterQueueDeletedAndRecreated_ReachesTheNewQueue()
    {
        var deadLetter = await QueueAsync("recreated-dlq");
        var source = await QueueAsync("recreated-source", new()
        {
            ["RedrivePolicy"] = $$"""{"deadLetterTargetArn":"{{deadLetter.Arn}}","maxReceiveCount":1}""",
            ["VisibilityTimeout"] = "0",
        });
        await _sqs.DeleteQueueAsync(deadLetter.Url);

        await _sqs.SendMessageAsync(source.Url, "first");
        (await ReceiveAllAsync(source.Url)).ShouldHaveSingleItem();
        // With its dead-letter queue gone, the message stays where it is.
        (await ReceiveAllAsync(source.Url)).ShouldHaveSingleItem().Body.ShouldBe("first");

        var recreated = await QueueAsync("recreated-dlq");
        recreated.Arn.ShouldBe(deadLetter.Arn);
        (await _sqs.ListDeadLetterSourceQueuesAsync(new ListDeadLetterSourceQueuesRequest { QueueUrl = recreated.Url }))
            .QueueUrls.ShouldBe([source.Url]);

        (await ReceiveAllAsync(source.Url)).ShouldBeEmpty();
        (await ReceiveAllAsync(recreated.Url)).ShouldHaveSingleItem().Body.ShouldBe("first");
    }
}

using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Shouldly;

namespace LocalSqsSnsMessaging.Tests.Sns;

/// <summary>
/// A topic accepts 256 KiB by default and up to 1 MiB once it sets the <c>MaximumMessageSize</c>
/// attribute, which in turn limits it to 100 SQS-style subscriptions.
/// See https://aws.amazon.com/about-aws/whats-new/2026/09/amazon-sns-1mib-support/
/// </summary>
public sealed class SnsMaximumMessageSizeTests : IDisposable
{
    private const int DefaultLimit = 262144;
    private const string OneMiB = "1048576";

    private readonly InMemoryAwsBus _bus = new();
    private readonly AmazonSimpleNotificationServiceClient _sns;
    private readonly AmazonSQSClient _sqs;

    public SnsMaximumMessageSizeTests()
    {
        _sns = _bus.CreateSnsClient();
        _sqs = _bus.CreateSqsClient();
    }

    public void Dispose()
    {
        _sns.Dispose();
        _sqs.Dispose();
    }

    private async Task<string> CreateTopicAsync(string name, Dictionary<string, string>? attributes = null) =>
        (await _sns.CreateTopicAsync(new CreateTopicRequest { Name = name, Attributes = attributes ?? [] })).TopicArn;

    private async Task<(string QueueUrl, string QueueArn)> CreateQueueAsync(string name)
    {
        var url = (await _sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name })).QueueUrl;
        var arn = (await _sqs.GetQueueAttributesAsync(url, ["QueueArn"])).Attributes["QueueArn"];
        return (url, arn);
    }

    private async Task<string> SubscribeQueueAsync(string topicArn, string queueName)
    {
        var (_, queueArn) = await CreateQueueAsync(queueName);
        var response = await _sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "sqs",
            Endpoint = queueArn
        });
        return response.SubscriptionArn;
    }

    private static PublishRequest PublishOfSize(string topicArn, int bytes) =>
        new() { TopicArn = topicArn, Message = new string('x', bytes) };

    [Test]
    public async Task Publish_AboveTheDefaultLimit_IsRejected()
    {
        var topicArn = await CreateTopicAsync("default-limit-topic");

        var exception = await Assert.ThrowsAsync<InvalidParameterException>(
            () => _sns.PublishAsync(PublishOfSize(topicArn, DefaultLimit + 1)));

        exception.ShouldNotBeNull().Message.ShouldBe("Invalid parameter: Message too long");
    }

    [Test]
    public async Task CreateTopic_CanOptInToLargerMessages()
    {
        var topicArn = await CreateTopicAsync("created-large-topic",
            new Dictionary<string, string> { ["MaximumMessageSize"] = OneMiB });

        // A full 1 MiB leaves no room for the traceparent the SDK would otherwise add.
        using var _ = AwsSdkTracing.SuppressPropagation();
        var response = await _sns.PublishAsync(PublishOfSize(topicArn, 1024 * 1024));

        response.MessageId.ShouldNotBeNullOrEmpty();
    }

    [Test]
    public async Task Publish_OneByteOverTheConfiguredLimit_IsRejected()
    {
        var topicArn = await CreateTopicAsync("exact-limit-topic",
            new Dictionary<string, string> { ["MaximumMessageSize"] = OneMiB });

        // Suppressed so the one byte over is the test's byte, not a propagated trace header.
        using var _ = AwsSdkTracing.SuppressPropagation();
        await Assert.ThrowsAsync<InvalidParameterException>(
            () => _sns.PublishAsync(PublishOfSize(topicArn, (1024 * 1024) + 1)));
    }

    [Test]
    public async Task SetTopicAttributes_CanOptInToLargerMessages_AndTheMessageIsDelivered()
    {
        var topicArn = await CreateTopicAsync("configured-large-topic");
        var (queueUrl, queueArn) = await CreateQueueAsync("large-message-queue");
        await _sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "sqs",
            Endpoint = queueArn,
            Attributes = new Dictionary<string, string> { ["RawMessageDelivery"] = "true" }
        });

        await _sns.SetTopicAttributesAsync(new SetTopicAttributesRequest
        {
            TopicArn = topicArn,
            AttributeName = "MaximumMessageSize",
            AttributeValue = OneMiB
        });

        var body = new string('x', 512 * 1024);
        await _sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = body });

        var received = await _sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl });
        received.Messages.ShouldNotBeNull().ShouldHaveSingleItem().Body.ShouldBe(body);
    }

    [Test]
    public async Task Publish_AboveTheConfiguredLimit_IsRejected()
    {
        var topicArn = await CreateTopicAsync("small-limit-topic",
            new Dictionary<string, string> { ["MaximumMessageSize"] = "2048" });

        var exception = await Assert.ThrowsAsync<InvalidParameterException>(
            () => _sns.PublishAsync(PublishOfSize(topicArn, 2049)));

        exception.ShouldNotBeNull().Message.ShouldBe("Invalid parameter: Message too long");
    }

    [Test]
    public async Task PublishBatch_IsMeasuredAgainstTheTopicsLimit()
    {
        var topicArn = await CreateTopicAsync("batch-large-topic",
            new Dictionary<string, string> { ["MaximumMessageSize"] = OneMiB });

        // Both entries together are over 256 KiB, which the topic now allows.
        var response = await _sns.PublishBatchAsync(new PublishBatchRequest
        {
            TopicArn = topicArn,
            PublishBatchRequestEntries =
            [
                new PublishBatchRequestEntry { Id = "1", Message = new string('x', 200_000) },
                new PublishBatchRequestEntry { Id = "2", Message = new string('y', 200_000) }
            ]
        });

        response.Successful.ShouldNotBeNull().Count.ShouldBe(2);
    }

    [Test]
    public async Task PublishBatch_AboveTheConfiguredLimit_IsRejected()
    {
        var topicArn = await CreateTopicAsync("batch-limited-topic",
            new Dictionary<string, string> { ["MaximumMessageSize"] = "300000" });

        await Assert.ThrowsAsync<Amazon.SimpleNotificationService.Model.BatchRequestTooLongException>(() => _sns.PublishBatchAsync(new PublishBatchRequest
        {
            TopicArn = topicArn,
            PublishBatchRequestEntries =
            [
                new PublishBatchRequestEntry { Id = "1", Message = new string('x', 200_000) },
                new PublishBatchRequestEntry { Id = "2", Message = new string('y', 200_000) }
            ]
        }));
    }

    [Test]
    [Arguments("not-a-number")]
    [Arguments("")]
    [Arguments("1023")]
    [Arguments("1048577")]
    public async Task SetTopicAttributes_RejectsAnInvalidMaximumMessageSize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var topicArn = await CreateTopicAsync($"invalid-size-topic-{value.GetHashCode(StringComparison.Ordinal)}");

        var exception = await Assert.ThrowsAsync<InvalidParameterException>(() => _sns.SetTopicAttributesAsync(new SetTopicAttributesRequest
        {
            TopicArn = topicArn,
            AttributeName = "MaximumMessageSize",
            AttributeValue = value
        }));

        exception.ShouldNotBeNull().Message.ShouldBe(
            $"Invalid parameter: MaximumMessageSize: {value} is not an integer between 1024 and 1048576 bytes");
    }

    [Test]
    public async Task CreateTopic_RejectsAnInvalidMaximumMessageSize()
    {
        var exception = await Assert.ThrowsAsync<InvalidParameterException>(() => CreateTopicAsync("invalid-created-topic",
            new Dictionary<string, string> { ["MaximumMessageSize"] = "1048577" }));

        // CreateTopic reports attribute failures through its attribute map.
        exception.ShouldNotBeNull().Message.ShouldBe(
            "Invalid parameter: Attributes Reason: MaximumMessageSize: 1048577 is not an integer between 1024 and 1048576 bytes");
    }

    [Test]
    public async Task GetTopicAttributes_ReportsTheLimitOnlyOnceTheTopicSetsOne()
    {
        var defaultTopicArn = await CreateTopicAsync("reported-default-topic");
        var largeTopicArn = await CreateTopicAsync("reported-large-topic",
            new Dictionary<string, string> { ["MaximumMessageSize"] = OneMiB });

        var defaults = await _sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = defaultTopicArn });
        var large = await _sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = largeTopicArn });

        // AWS leaves the attribute out entirely until the topic opts in.
        defaults.Attributes.ShouldNotContainKey("MaximumMessageSize");
        large.Attributes["MaximumMessageSize"].ShouldBe("1048576");
    }

    [Test]
    public async Task Subscribe_RejectsAnHttpEndpointOnALargeMessageTopic()
    {
        var topicArn = await CreateTopicAsync("no-http-topic",
            new Dictionary<string, string> { ["MaximumMessageSize"] = OneMiB });

        var exception = await Assert.ThrowsAsync<InvalidParameterException>(() => _sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "https",
            Endpoint = "https://example.test/sns-webhook"
        }));

        exception.ShouldNotBeNull().Message.ShouldBe(
            "Invalid parameter: MaximumMessageSize greater than 262144 bytes is not supported for the following protocol: [https]");
    }

    [Test]
    public async Task SetTopicAttributes_RejectsLargeMessagesWhenTheTopicHasAnHttpSubscription()
    {
        var topicArn = await CreateTopicAsync("existing-http-topic");
        await _sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "https",
            Endpoint = "https://example.test/sns-webhook"
        });

        // The subscription is still pending confirmation, which AWS counts all the same.
        var exception = await Assert.ThrowsAsync<InvalidParameterException>(() => _sns.SetTopicAttributesAsync(new SetTopicAttributesRequest
        {
            TopicArn = topicArn,
            AttributeName = "MaximumMessageSize",
            AttributeValue = OneMiB
        }));

        exception.ShouldNotBeNull().Message.ShouldBe(
            "Invalid parameter: MaximumMessageSize greater than 262144 bytes is not supported for the following protocol: [https]");
    }

    [Test]
    public async Task SetTopicAttributes_AllowsTheDefaultLimitOnATopicWithAnHttpSubscription()
    {
        var topicArn = await CreateTopicAsync("boundary-http-topic");
        await _sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "https",
            Endpoint = "https://example.test/sns-webhook"
        });

        // Only a limit *above* 256 KiB rules HTTP/S out; 256 KiB itself is fine.
        await _sns.SetTopicAttributesAsync(new SetTopicAttributesRequest
        {
            TopicArn = topicArn,
            AttributeName = "MaximumMessageSize",
            AttributeValue = "262144"
        });

        var attributes = await _sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = topicArn });
        attributes.Attributes["MaximumMessageSize"].ShouldBe("262144");
    }

    [Test]
    public async Task Publish_ToAMissingTopic_ReportsTheMissingTopicRatherThanTheSize()
    {
        var missingTopicArn = $"arn:aws:sns:us-east-1:{_bus.CurrentAccountId}:no-such-topic";

        await Assert.ThrowsAsync<NotFoundException>(
            () => _sns.PublishAsync(PublishOfSize(missingTopicArn, DefaultLimit + 1)));
    }

    [Test]
    public async Task Publish_AtTheLimit_IsDeliveredEvenThoughTheEnvelopePushesItOverSqsOwnLimit()
    {
        var topicArn = await CreateTopicAsync("envelope-overflow-topic",
            new Dictionary<string, string> { ["MaximumMessageSize"] = OneMiB });
        var (queueUrl, queueArn) = await CreateQueueAsync("envelope-overflow-queue");
        await _sns.SubscribeAsync(new SubscribeRequest { TopicArn = topicArn, Protocol = "sqs", Endpoint = queueArn });

        // The SNS envelope goes on top of the message, so what reaches SQS is above SQS's own 1 MiB
        // limit - and AWS delivers it anyway.
        var body = new string('x', 1024 * 1024);
        using (AwsSdkTracing.SuppressPropagation())
        {
            await _sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = body });
        }

        var received = await _sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl });
        var envelope = received.Messages.ShouldNotBeNull().ShouldHaveSingleItem();
        envelope.Body.Length.ShouldBeGreaterThan(1024 * 1024);
    }

    [Test]
    public async Task Publish_LargePercentEncodedPayload_SurvivesTheQueryProtocol()
    {
        var topicArn = await CreateTopicAsync("encoded-payload-topic",
            new Dictionary<string, string> { ["MaximumMessageSize"] = OneMiB });
        var (queueUrl, queueArn) = await CreateQueueAsync("encoded-payload-queue");
        await _sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "sqs",
            Endpoint = queueArn,
            Attributes = new Dictionary<string, string> { ["RawMessageDelivery"] = "true" }
        });

        // Realistic JSON: every quote, colon and brace is percent-encoded on the wire, so the whole
        // payload takes the query parser's decoding path rather than its verbatim fast path.
        var body = "{\"payload\":\"" + new string('a', 900_000) + "\",\"kind\":\"large\"}";
        await _sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = body });

        var received = await _sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl });
        received.Messages.ShouldNotBeNull().ShouldHaveSingleItem().Body.ShouldBe(body);
    }

    [Test]
    public async Task Subscribe_RejectsMoreThanAHundredSubscriptionsOnALargeMessageTopic()
    {
        var topicArn = await CreateTopicAsync("hundred-subscription-topic",
            new Dictionary<string, string> { ["MaximumMessageSize"] = OneMiB });

        for (var i = 0; i < 100; i++)
        {
            await SubscribeQueueAsync(topicArn, $"hundred-subscription-queue-{i}");
        }

        var exception = await Assert.ThrowsAsync<InvalidParameterException>(
            () => SubscribeQueueAsync(topicArn, "hundred-subscription-queue-overflow"));

        exception.ShouldNotBeNull().Message.ShouldBe(
            "Invalid parameter: A topic with MaximumMessageSize greater than 262144 bytes supports a maximum of 100 subscriptions");
    }

    [Test]
    public async Task SetTopicAttributes_DoesNotPoliceTheSubscriptionCountWhenRaisingTheLimit()
    {
        var topicArn = await CreateTopicAsync("over-capacity-topic");
        for (var i = 0; i < 101; i++)
        {
            await SubscribeQueueAsync(topicArn, $"over-capacity-queue-{i}");
        }

        // AWS lets the limit go up on a topic that is already past 100 subscriptions; only the next
        // Subscribe is turned away.
        await _sns.SetTopicAttributesAsync(new SetTopicAttributesRequest
        {
            TopicArn = topicArn,
            AttributeName = "MaximumMessageSize",
            AttributeValue = OneMiB
        });

        var attributes = await _sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = topicArn });
        attributes.Attributes["MaximumMessageSize"].ShouldBe("1048576");
    }
}

using System.Net;
using System.Text.Json;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using LocalSqsSnsMessaging.Http;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using MessageAttributeValue = Amazon.SimpleNotificationService.Model.MessageAttributeValue;

namespace LocalSqsSnsMessaging.Tests.Sns;

public sealed class SnsHttpSubscriptionTests : IDisposable
{
    private const string Endpoint = "https://example.test/sns-webhook";

    private readonly FakeTimeProvider _timeProvider = new();
    private readonly RecordingHttpHandler _endpoint = new();
    private readonly InMemoryAwsBus _bus;
    private readonly AmazonSimpleNotificationServiceClient _sns;
    private readonly AmazonSQSClient _sqs;

    public SnsHttpSubscriptionTests()
    {
        _bus = new InMemoryAwsBus
        {
            TimeProvider = _timeProvider,
            HttpClient = new HttpClient(_endpoint)
        };
        _sns = _bus.CreateSnsClient();
        _sqs = _bus.CreateSqsClient();
    }

    public void Dispose()
    {
        _sns.Dispose();
        _sqs.Dispose();
        _bus.HttpClient.Dispose();
        _endpoint.Dispose();
    }

    private async Task<string> CreateTopicAsync(string name = "http-topic") =>
        (await _sns.CreateTopicAsync(new CreateTopicRequest { Name = name })).TopicArn;

    private async Task<(string QueueUrl, string QueueArn)> CreateQueueAsync(string name)
    {
        var url = (await _sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name })).QueueUrl;
        var arn = (await _sqs.GetQueueAttributesAsync(url, ["QueueArn"])).Attributes["QueueArn"];
        return (url, arn);
    }

    /// <summary>
    /// Subscribes an HTTP/S endpoint and completes the confirmation handshake the way an endpoint
    /// would: wait for the SubscriptionConfirmation POST, then confirm with its token.
    /// </summary>
    private async Task<string> SubscribeAndConfirmAsync(string topicArn, string protocol = "https", string endpoint = Endpoint, Dictionary<string, string>? attributes = null)
    {
        var confirmationsBefore = _endpoint.RequestsOfType("SubscriptionConfirmation").Count;
        var subscribe = await _sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = protocol,
            Endpoint = endpoint,
            Attributes = attributes ?? [],
            ReturnSubscriptionArn = true
        });

        await RecordingHttpHandler.WaitForRequestsAsync(() => _endpoint.RequestsOfType("SubscriptionConfirmation").Count, confirmationsBefore + 1);
        var confirmation = _endpoint.RequestsOfType("SubscriptionConfirmation").Last();
        var token = JsonDocument.Parse(confirmation.Body).RootElement.GetProperty("Token").GetString()!;

        var confirmed = await _sns.ConfirmSubscriptionAsync(new ConfirmSubscriptionRequest { TopicArn = topicArn, Token = token });
        confirmed.SubscriptionArn.ShouldBe(subscribe.SubscriptionArn);
        return subscribe.SubscriptionArn;
    }

    [Test]
    public async Task Subscribe_ToHttpsEndpoint_IsPendingUntilConfirmed()
    {
        var topicArn = await CreateTopicAsync();

        // Without ReturnSubscriptionArn the ARN is withheld until the endpoint confirms, as on AWS.
        var withoutArn = await _sns.SubscribeAsync(new SubscribeRequest { TopicArn = topicArn, Protocol = "https", Endpoint = Endpoint });
        withoutArn.SubscriptionArn.ShouldBe("pending confirmation");

        var subscribe = await _sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "https",
            Endpoint = Endpoint,
            ReturnSubscriptionArn = true
        });
        subscribe.SubscriptionArn.ShouldNotBe("pending confirmation");

        await RecordingHttpHandler.WaitForRequestsAsync(() => _endpoint.RequestsOfType("SubscriptionConfirmation").Count, 2);

        var confirmation = _endpoint.RequestsOfType("SubscriptionConfirmation").Last();
        confirmation.Uri.ToString().ShouldBe(Endpoint);
        confirmation.Headers["x-amz-sns-topic-arn"].ShouldBe(topicArn);
        confirmation.Headers["x-amz-sns-subscription-arn"].ShouldBe(subscribe.SubscriptionArn);
        confirmation.ContentType.ShouldBe("text/plain; charset=UTF-8");

        var body = JsonDocument.Parse(confirmation.Body).RootElement;
        body.GetProperty("Type").GetString().ShouldBe("SubscriptionConfirmation");
        body.GetProperty("TopicArn").GetString().ShouldBe(topicArn);
        var token = body.GetProperty("Token").GetString()!;
        body.GetProperty("SubscribeURL").GetString()!.ShouldContain($"Token={token}");

        (await _sns.GetSubscriptionAttributesAsync(subscribe.SubscriptionArn)).Attributes["PendingConfirmation"].ShouldBe("true");
        (await _sns.ListSubscriptionsByTopicAsync(topicArn)).Subscriptions
            .Select(s => s.SubscriptionArn).ShouldAllBe(arn => arn == "PendingConfirmation");

        // Nothing is delivered while pending.
        await _sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = "too early" });
        await Task.Delay(50);
        _endpoint.RequestsOfType("Notification").ShouldBeEmpty();

        var confirmed = await _sns.ConfirmSubscriptionAsync(new ConfirmSubscriptionRequest { TopicArn = topicArn, Token = token });
        confirmed.SubscriptionArn.ShouldBe(subscribe.SubscriptionArn);

        var attributes = await _sns.GetSubscriptionAttributesAsync(subscribe.SubscriptionArn);
        attributes.Attributes["Protocol"].ShouldBe("https");
        attributes.Attributes["Endpoint"].ShouldBe(Endpoint);
        attributes.Attributes["PendingConfirmation"].ShouldBe("false");
        (await _sns.ListSubscriptionsByTopicAsync(topicArn)).Subscriptions
            .Select(s => s.SubscriptionArn).ShouldContain(subscribe.SubscriptionArn);

        await _sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = "now" });
        await RecordingHttpHandler.WaitForRequestsAsync(() => _endpoint.RequestsOfType("Notification").Count, 1);
        _endpoint.RequestsOfType("Notification").ShouldHaveSingleItem();
    }

    [Test]
    public async Task VisitingSubscribeUrl_ConfirmsTheSubscription()
    {
        var topicArn = await CreateTopicAsync();
        var subscribe = await _sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "https",
            Endpoint = Endpoint,
            ReturnSubscriptionArn = true
        });

        await RecordingHttpHandler.WaitForRequestsAsync(() => _endpoint.RequestsOfType("SubscriptionConfirmation").Count, 1);
        var subscribeUrl = JsonDocument.Parse(_endpoint.RequestsOfType("SubscriptionConfirmation").Single().Body)
            .RootElement.GetProperty("SubscribeURL").GetString()!;

        // What a real endpoint does with the handshake: GET the SubscribeURL. Route it through the
        // in-memory handler, which is what the server does for a real GET.
        using var handler = new InMemoryAwsHttpMessageHandler(_bus, AwsServiceType.Sns);
        using var http = new HttpClient(handler);
        using var response = await http.GetAsync(new Uri(subscribeUrl));

        response.IsSuccessStatusCode.ShouldBeTrue();
        (await response.Content.ReadAsStringAsync()).ShouldContain(subscribe.SubscriptionArn);
        (await _sns.GetSubscriptionAttributesAsync(subscribe.SubscriptionArn)).Attributes["PendingConfirmation"].ShouldBe("false");
    }

    [Test]
    public async Task ConfirmSubscription_WithUnknownToken_Throws()
    {
        var topicArn = await CreateTopicAsync();

        await Assert.ThrowsAsync<InvalidParameterException>(() => _sns.ConfirmSubscriptionAsync(new ConfirmSubscriptionRequest
        {
            TopicArn = topicArn,
            Token = "nope"
        }));
    }

    [Test]
    public async Task Subscribe_WithEndpointNotMatchingProtocol_Throws()
    {
        var topicArn = await CreateTopicAsync();

        await Assert.ThrowsAsync<InvalidParameterException>(() => _sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "https",
            Endpoint = "http://example.test/not-https"
        }));

        await Assert.ThrowsAsync<InvalidParameterException>(() => _sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "http",
            Endpoint = "not a url"
        }));
    }

    [Test]
    public async Task Publish_ToHttpSubscription_PostsSnsEnvelopeWithHeaders()
    {
        var topicArn = await CreateTopicAsync();
        var subscriptionArn = await SubscribeAndConfirmAsync(topicArn);

        var publish = await _sns.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn,
            Subject = "Hello",
            Message = "Hello, World!",
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                ["eventType"] = new() { DataType = "String", StringValue = "greeting" }
            }
        });

        await RecordingHttpHandler.WaitForRequestsAsync(() => _endpoint.RequestsOfType("Notification").Count, 1);

        var notification = _endpoint.RequestsOfType("Notification").ShouldHaveSingleItem();
        notification.Headers["x-amz-sns-message-id"].ShouldBe(publish.MessageId);
        notification.Headers["x-amz-sns-topic-arn"].ShouldBe(topicArn);
        notification.Headers["x-amz-sns-subscription-arn"].ShouldBe(subscriptionArn);
        notification.Headers.ShouldNotContainKey("x-amz-sns-rawdelivery");
        notification.Headers["User-Agent"].ShouldBe("Amazon Simple Notification Service Agent");

        var body = JsonDocument.Parse(notification.Body).RootElement;
        body.GetProperty("Type").GetString().ShouldBe("Notification");
        body.GetProperty("MessageId").GetString().ShouldBe(publish.MessageId);
        body.GetProperty("TopicArn").GetString().ShouldBe(topicArn);
        body.GetProperty("Subject").GetString().ShouldBe("Hello");
        body.GetProperty("Message").GetString().ShouldBe("Hello, World!");
        body.GetProperty("UnsubscribeURL").GetString()!.ShouldContain("Action=Unsubscribe");
        body.GetProperty("MessageAttributes").GetProperty("eventType").GetProperty("Value").GetString().ShouldBe("greeting");
    }

    [Test]
    public async Task Publish_ToRawHttpSubscription_PostsBareBodyWithRawDeliveryHeader()
    {
        var topicArn = await CreateTopicAsync();
        await SubscribeAndConfirmAsync(topicArn, attributes: new Dictionary<string, string>
        {
            ["RawMessageDelivery"] = "true",
            ["DeliveryPolicy"] = """{"requestPolicy": {"headerContentType": "application/json"}}"""
        });

        await _sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = """{"hello":"world"}""" });

        await RecordingHttpHandler.WaitForRequestsAsync(() => _endpoint.RequestsOfType("Notification").Count, 1);

        var notification = _endpoint.RequestsOfType("Notification").ShouldHaveSingleItem();
        notification.Headers["x-amz-sns-rawdelivery"].ShouldBe("true");
        notification.ContentType.ShouldBe("application/json");
        notification.Body.ShouldBe("""{"hello":"world"}""");
    }

    [Test]
    public async Task Publish_ToHttpSubscription_HonoursFilterPolicy()
    {
        var topicArn = await CreateTopicAsync();
        await SubscribeAndConfirmAsync(topicArn, attributes: new Dictionary<string, string>
        {
            ["RawMessageDelivery"] = "true",
            ["FilterPolicy"] = """{"eventType": ["wanted"]}"""
        });

        await _sns.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn,
            Message = "filtered out",
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                ["eventType"] = new() { DataType = "String", StringValue = "unwanted" }
            }
        });
        await _sns.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn,
            Message = "delivered",
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                ["eventType"] = new() { DataType = "String", StringValue = "wanted" }
            }
        });

        await RecordingHttpHandler.WaitForRequestsAsync(() => _endpoint.RequestsOfType("Notification").Count, 1);

        _endpoint.RequestsOfType("Notification").Select(r => r.Body).ShouldBe(["delivered"]);
    }

    [Test]
    public async Task Publish_WhenEndpointReturns5xx_RetriesPerDeliveryPolicy_ThenDeadLetters()
    {
        var topicArn = await CreateTopicAsync();
        var (dlqUrl, dlqArn) = await CreateQueueAsync("http-dlq");
        await SubscribeAndConfirmAsync(topicArn, attributes: new Dictionary<string, string>
        {
            ["RedrivePolicy"] = $$"""{"deadLetterTargetArn": "{{dlqArn}}"}"""
        });
        _endpoint.Respond = _ => HttpStatusCode.ServiceUnavailable;

        var publish = await _sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = "flaky" });

        // Default policy: 3 retries, 20 seconds apart.
        await RecordingHttpHandler.WaitForRequestsAsync(() => _endpoint.RequestsOfType("Notification").Count, 1);
        _endpoint.RequestsOfType("Notification").Count.ShouldBe(1);
        (await _sqs.ReceiveMessageAsync(dlqUrl)).Messages.ShouldBeEmptyAwsCollection();

        _timeProvider.Advance(TimeSpan.FromSeconds(19));
        await Task.Delay(50);
        _endpoint.RequestsOfType("Notification").Count.ShouldBe(1);

        _timeProvider.Advance(TimeSpan.FromSeconds(1));
        await RecordingHttpHandler.WaitForRequestsAsync(() => _endpoint.RequestsOfType("Notification").Count, 2);
        _endpoint.RequestsOfType("Notification").Count.ShouldBe(2);

        _timeProvider.Advance(TimeSpan.FromSeconds(20));
        await RecordingHttpHandler.WaitForRequestsAsync(() => _endpoint.RequestsOfType("Notification").Count, 3);

        _timeProvider.Advance(TimeSpan.FromSeconds(20));
        await RecordingHttpHandler.WaitForRequestsAsync(() => _endpoint.RequestsOfType("Notification").Count, 4);
        _endpoint.RequestsOfType("Notification").Count.ShouldBe(4);

        // Every attempt carried the same SNS message ID.
        _endpoint.RequestsOfType("Notification").Select(r => r.Headers["x-amz-sns-message-id"]).Distinct().ShouldBe([publish.MessageId]);

        // After the final retry the message is moved to the DLQ as the SNS envelope.
        List<Message> dlqMessages = [];
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (dlqMessages.Count == 0 && DateTime.UtcNow < deadline)
        {
            dlqMessages = (await _sqs.ReceiveMessageAsync(dlqUrl)).Messages ?? [];
        }
        var dead = dlqMessages.ShouldHaveSingleItem();
        var body = JsonDocument.Parse(dead.Body).RootElement;
        body.GetProperty("Type").GetString().ShouldBe("Notification");
        body.GetProperty("MessageId").GetString().ShouldBe(publish.MessageId);
        body.GetProperty("Message").GetString().ShouldBe("flaky");

        // The DLQ gets the exact body that was posted, not a regenerated envelope with a later timestamp.
        dead.Body.ShouldBe(_endpoint.RequestsOfType("Notification")[0].Body);

        // Nothing further is attempted.
        _timeProvider.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(50);
        _endpoint.RequestsOfType("Notification").Count.ShouldBe(4);
    }

    [Test]
    public async Task Publish_WhenEndpointReturns4xx_DoesNotRetry_AndDeadLettersImmediately()
    {
        var topicArn = await CreateTopicAsync();
        var (dlqUrl, dlqArn) = await CreateQueueAsync("http-dlq");
        await SubscribeAndConfirmAsync(topicArn, attributes: new Dictionary<string, string>
        {
            ["RawMessageDelivery"] = "true",
            ["RedrivePolicy"] = $$"""{"deadLetterTargetArn": "{{dlqArn}}"}"""
        });
        _endpoint.Respond = _ => HttpStatusCode.NotFound;

        await _sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = "gone" });

        await RecordingHttpHandler.WaitForRequestsAsync(() => _endpoint.RequestsOfType("Notification").Count, 1);
        _timeProvider.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(50);
        _endpoint.RequestsOfType("Notification").Count.ShouldBe(1);

        var dead = (await _sqs.ReceiveMessageAsync(dlqUrl)).Messages.ShouldHaveSingleItem();
        dead.Body.ShouldBe("gone");
    }

    [Test]
    public async Task Publish_WhenEndpointUnreachable_UsesCustomDeliveryPolicy()
    {
        var topicArn = await CreateTopicAsync();
        await SubscribeAndConfirmAsync(topicArn, "http", "http://example.test/hook", new Dictionary<string, string>
        {
            ["DeliveryPolicy"] = """{"healthyRetryPolicy": {"numRetries": 2, "numNoDelayRetries": 1, "minDelayTarget": 5, "maxDelayTarget": 5}}"""
        });
        _endpoint.ThrowOnSend = new HttpRequestException("connection refused");

        await _sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = "unreachable" });

        // Initial attempt plus one immediate retry.
        await RecordingHttpHandler.WaitForRequestsAsync(() => _endpoint.RequestsOfType("Notification").Count, 2);
        _endpoint.RequestsOfType("Notification").Count.ShouldBe(2);

        _timeProvider.Advance(TimeSpan.FromSeconds(5));
        await RecordingHttpHandler.WaitForRequestsAsync(() => _endpoint.RequestsOfType("Notification").Count, 3);
        _endpoint.RequestsOfType("Notification").Count.ShouldBe(3);

        _timeProvider.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(50);
        _endpoint.RequestsOfType("Notification").Count.ShouldBe(3);
    }

    [Test]
    public async Task Publish_UsesTopicLevelDeliveryPolicy_WhenSubscriptionHasNone()
    {
        var topicArn = await CreateTopicAsync();
        await _sns.SetTopicAttributesAsync(new SetTopicAttributesRequest
        {
            TopicArn = topicArn,
            AttributeName = "DeliveryPolicy",
            AttributeValue = """{"http": {"defaultHealthyRetryPolicy": {"numRetries": 1, "minDelayTarget": 7, "maxDelayTarget": 7}}}"""
        });
        await SubscribeAndConfirmAsync(topicArn);
        _endpoint.Respond = _ => HttpStatusCode.BadGateway;

        await _sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = "topic policy" });

        await RecordingHttpHandler.WaitForRequestsAsync(() => _endpoint.RequestsOfType("Notification").Count, 1);
        _timeProvider.Advance(TimeSpan.FromSeconds(7));
        await RecordingHttpHandler.WaitForRequestsAsync(() => _endpoint.RequestsOfType("Notification").Count, 2);

        // One retry, not the default three.
        _timeProvider.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(50);
        _endpoint.RequestsOfType("Notification").Count.ShouldBe(2);
    }

    [Test]
    public async Task Publish_ToDeletedQueue_DeadLettersViaSubscriptionRedrivePolicy()
    {
        var topicArn = await CreateTopicAsync();
        var (queueUrl, queueArn) = await CreateQueueAsync("main-queue");
        var (dlqUrl, dlqArn) = await CreateQueueAsync("subscription-dlq");

        var subscribe = await _sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "sqs",
            Endpoint = queueArn,
            Attributes = new Dictionary<string, string>
            {
                ["RawMessageDelivery"] = "true",
                ["RedrivePolicy"] = $$"""{"deadLetterTargetArn": "{{dlqArn}}"}"""
            }
        });

        var attributes = await _sns.GetSubscriptionAttributesAsync(subscribe.SubscriptionArn);
        attributes.Attributes["RedrivePolicy"].ShouldContain(dlqArn);

        await _sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = "before delete" });
        (await _sqs.ReceiveMessageAsync(queueUrl)).Messages.ShouldHaveSingleItem().Body.ShouldBe("before delete");

        await _sqs.DeleteQueueAsync(queueUrl);
        await _sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = "after delete" });

        (await _sqs.ReceiveMessageAsync(dlqUrl)).Messages.ShouldHaveSingleItem().Body.ShouldBe("after delete");
    }

    [Test]
    public async Task SetSubscriptionAttributes_RejectsInvalidRedriveAndDeliveryPolicies()
    {
        var topicArn = await CreateTopicAsync();
        var (_, queueArn) = await CreateQueueAsync("main-queue");
        var subscribe = await _sns.SubscribeAsync(new SubscribeRequest { TopicArn = topicArn, Protocol = "sqs", Endpoint = queueArn });

        await Assert.ThrowsAsync<InvalidParameterException>(() => _sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
        {
            SubscriptionArn = subscribe.SubscriptionArn,
            AttributeName = "RedrivePolicy",
            AttributeValue = """{"deadLetterTargetArn": "arn:aws:sns:us-east-1:000000000000:not-a-queue"}"""
        }));

        await Assert.ThrowsAsync<InvalidParameterException>(() => _sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
        {
            SubscriptionArn = subscribe.SubscriptionArn,
            AttributeName = "DeliveryPolicy",
            AttributeValue = "[]"
        }));

        await Assert.ThrowsAsync<InvalidParameterException>(() => _sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
        {
            SubscriptionArn = subscribe.SubscriptionArn,
            AttributeName = "FilterPolicyScope",
            AttributeValue = "Everything"
        }));

        // Clearing a redrive policy with an empty object removes it.
        await _sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
        {
            SubscriptionArn = subscribe.SubscriptionArn,
            AttributeName = "RedrivePolicy",
            AttributeValue = "{}"
        });
        var attributes = await _sns.GetSubscriptionAttributesAsync(subscribe.SubscriptionArn);
        attributes.Attributes.ShouldNotContainKey("RedrivePolicy");
    }
}

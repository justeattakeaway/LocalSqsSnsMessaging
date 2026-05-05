using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.SQS.Model;
using LocalSqsSnsMessaging.Generic;
using Shouldly;

namespace LocalSqsSnsMessaging.Tests.SdkClient;

[JsonSerializable(typeof(SqsTypedReceiveTests.OrderEvent))]
internal sealed partial class OrderEventJsonContext : JsonSerializerContext;

/// <summary>
/// Experimental: typed ReceiveMessageAsync&lt;T&gt; via custom AWS SDK marshaller.
/// Mirrors the shape of HttpClientJsonExtensions.GetFromJsonAsync&lt;T&gt; — the
/// caller skips the intermediate string body and gets a deserialized T.
/// </summary>
public sealed class SqsTypedReceiveTests
{
    internal sealed record OrderEvent(int OrderId, string Customer, decimal Total);

    [Test]
    public async Task TypedReceive_DeserializesBody_IntoGenericType()
    {
        var bus = new InMemoryAwsBus();
        using var sqs = bus.CreateTypedSqsClient();
        var queueUrl = (await sqs.CreateQueueAsync("typed-queue")).QueueUrl;

        var payload = new OrderEvent(42, "Alice", 19.99m);
        await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = JsonSerializer.Serialize(payload, JsonSerializerOptions.Default),
        });

        var response = await sqs.ReceiveMessageAsync<OrderEvent>(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 1,
        });

        response.Messages.ShouldHaveSingleItem();
        var message = response.Messages[0];
        message.MessageId.ShouldNotBeNullOrEmpty();
        message.ReceiptHandle.ShouldNotBeNullOrEmpty();
        message.Body.ShouldBe(payload);
    }

    [Test]
    public async Task TypedReceive_HandlesEscapedStringsInBody()
    {
        var bus = new InMemoryAwsBus();
        using var sqs = bus.CreateTypedSqsClient();
        var queueUrl = (await sqs.CreateQueueAsync("typed-queue-escaped")).QueueUrl;

        // Quotes and backslashes force the JSON string to be escaped on the wire,
        // exercising the CopyString path in the unmarshaller.
        var payload = new OrderEvent(7, "Bob \"the builder\" \\ Co.", 1m);
        await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = JsonSerializer.Serialize(payload),
        });

        var response = await sqs.ReceiveMessageAsync<OrderEvent>(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 1,
        });

        response.Messages.ShouldHaveSingleItem();
        response.Messages[0].Body.ShouldBe(payload);
    }

    [Test]
    public async Task TypedReceive_ReturnsEmpty_WhenQueueIsEmpty()
    {
        var bus = new InMemoryAwsBus();
        using var sqs = bus.CreateTypedSqsClient();
        var queueUrl = (await sqs.CreateQueueAsync("typed-queue-empty")).QueueUrl;

        var response = await sqs.ReceiveMessageAsync<OrderEvent>(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 1,
            WaitTimeSeconds = 0,
        });

        response.Messages.ShouldBeEmpty();
    }

    [Test]
    public async Task TypedReceive_WithJsonTypeInfo_DeserializesViaSourceGen()
    {
        var bus = new InMemoryAwsBus();
        using var sqs = bus.CreateTypedSqsClient();
        var queueUrl = (await sqs.CreateQueueAsync("typed-queue-stj-source-gen")).QueueUrl;

        var payload = new OrderEvent(99, "Carol \"Source-gen\" \\ Co", 7.5m);
        await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = JsonSerializer.Serialize(payload, OrderEventJsonContext.Default.OrderEvent),
        });

        var response = await sqs.ReceiveMessageAsync(
            new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 1 },
            OrderEventJsonContext.Default.OrderEvent);

        response.Messages.ShouldHaveSingleItem();
        response.Messages[0].Body.ShouldBe(payload);
    }

    [Test]
    public async Task TypedReceive_WithJsonSerializerContext_DeserializesViaSourceGen()
    {
        var bus = new InMemoryAwsBus();
        using var sqs = bus.CreateTypedSqsClient();
        var queueUrl = (await sqs.CreateQueueAsync("typed-queue-stj-context")).QueueUrl;

        var payload = new OrderEvent(123, "Dave", 0.01m);
        await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = JsonSerializer.Serialize(payload, OrderEventJsonContext.Default.OrderEvent),
        });

        var response = await sqs.ReceiveMessageAsync<OrderEvent>(
            new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 1 },
            OrderEventJsonContext.Default);

        response.Messages.ShouldHaveSingleItem();
        response.Messages[0].Body.ShouldBe(payload);
    }

    [Test]
    public async Task TypedReceive_WithContextMissingType_Throws()
    {
        var bus = new InMemoryAwsBus();
        using var sqs = bus.CreateTypedSqsClient();
        var queueUrl = (await sqs.CreateQueueAsync("typed-queue-bad-context")).QueueUrl;

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await sqs.ReceiveMessageAsync<DateTimeOffset>(
                new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 1 },
                OrderEventJsonContext.Default));
    }

    [Test]
    public async Task TypedClient_StandardReceiveAsync_StillReturnsStringBody()
    {
        // The typed client subclass must remain a drop-in for the stock
        // AmazonSQSClient — calling the non-generic ReceiveMessageAsync should
        // continue to return a regular ReceiveMessageResponse with string Body.
        var bus = new InMemoryAwsBus();
        using var sqs = bus.CreateTypedSqsClient();
        var queueUrl = (await sqs.CreateQueueAsync("compat-queue")).QueueUrl;

        await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "plain text body",
        });

        var response = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 1,
        });

        response.Messages.ShouldHaveSingleItem();
        response.Messages[0].Body.ShouldBe("plain text body");
    }
}

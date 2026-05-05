using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.SQS.Model;
using Shouldly;

namespace LocalSqsSnsMessaging.Tests.SdkClient;

[JsonSerializable(typeof(SqsRawReceiveTests.OrderEvent))]
internal sealed partial class OrderEventJsonContext : JsonSerializerContext;

/// <summary>
/// Experimental: <see cref="RawAmazonSQSClient.ReceiveMessageRawAsync"/>
/// returns each message body as <see cref="ReadOnlyMemory{Byte}"/> UTF-8 bytes.
/// Callers feed those bytes into the deserializer of their choice.
/// </summary>
public sealed class SqsRawReceiveTests
{
    internal sealed record OrderEvent(int OrderId, string Customer, decimal Total);

    [Test]
    public async Task ReceiveMessageRaw_ReturnsBodyAsUtf8Bytes()
    {
        var bus = new InMemoryAwsBus();
        using var sqs = bus.CreateRawSqsClient();
        var queueUrl = (await sqs.CreateQueueAsync("raw-queue")).QueueUrl;

        var payload = new OrderEvent(42, "Alice", 19.99m);
        var bodyText = JsonSerializer.Serialize(payload, OrderEventJsonContext.Default.OrderEvent);
        await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = bodyText,
        });

        var response = await sqs.ReceiveMessageRawAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 1,
        });

        response.Messages.ShouldHaveSingleItem();
        var message = response.Messages[0];
        message.MessageId.ShouldNotBeNullOrEmpty();
        message.ReceiptHandle.ShouldNotBeNullOrEmpty();
        message.Body.IsEmpty.ShouldBeFalse();

        // Bytes should be the UTF-8 encoding of the original JSON body.
        Encoding.UTF8.GetString(message.Body.Span).ShouldBe(bodyText);

        // …and feed straight into a source-generated deserializer.
        var deserialized = JsonSerializer.Deserialize(message.Body.Span, OrderEventJsonContext.Default.OrderEvent);
        deserialized.ShouldBe(payload);
    }

    [Test]
    public async Task ReceiveMessageRaw_HandlesEscapedStringsInBody()
    {
        var bus = new InMemoryAwsBus();
        using var sqs = bus.CreateRawSqsClient();
        var queueUrl = (await sqs.CreateQueueAsync("raw-queue-escaped")).QueueUrl;

        // Quotes and backslashes force the JSON string to be escaped on the wire,
        // exercising the CopyString path in the unmarshaller.
        var payload = new OrderEvent(7, "Bob \"the builder\" \\ Co.", 1m);
        var bodyText = JsonSerializer.Serialize(payload, OrderEventJsonContext.Default.OrderEvent);
        await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = bodyText,
        });

        var response = await sqs.ReceiveMessageRawAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 1,
        });

        response.Messages.ShouldHaveSingleItem();
        Encoding.UTF8.GetString(response.Messages[0].Body.Span).ShouldBe(bodyText);
    }

    [Test]
    public async Task ReceiveMessageRaw_PassesNonJsonBodyThrough()
    {
        // Body bytes are agnostic to format — caller can use any serializer.
        var bus = new InMemoryAwsBus();
        using var sqs = bus.CreateRawSqsClient();
        var queueUrl = (await sqs.CreateQueueAsync("raw-queue-text")).QueueUrl;

        await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "plain text, no JSON",
        });

        var response = await sqs.ReceiveMessageRawAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 1,
        });

        response.Messages.ShouldHaveSingleItem();
        Encoding.UTF8.GetString(response.Messages[0].Body.Span).ShouldBe("plain text, no JSON");
    }

    [Test]
    public async Task ReceiveMessageRaw_ReturnsEmpty_WhenQueueIsEmpty()
    {
        var bus = new InMemoryAwsBus();
        using var sqs = bus.CreateRawSqsClient();
        var queueUrl = (await sqs.CreateQueueAsync("raw-queue-empty")).QueueUrl;

        var response = await sqs.ReceiveMessageRawAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 1,
            WaitTimeSeconds = 0,
        });

        response.Messages.ShouldBeEmpty();
    }

    [Test]
    public async Task RawClient_StandardReceiveAsync_StillReturnsStringBody()
    {
        // The raw client subclass must remain a drop-in for the stock
        // AmazonSQSClient — calling the standard ReceiveMessageAsync should
        // continue to return a regular ReceiveMessageResponse with string Body.
        var bus = new InMemoryAwsBus();
        using var sqs = bus.CreateRawSqsClient();
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

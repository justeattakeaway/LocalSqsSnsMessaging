using System.Net;
using System.Net.Sockets;
using Amazon.Runtime;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using LocalSqsSnsMessaging.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace LocalSqsSnsMessaging.Tests.Server;

/// <summary>
/// Integration smoke tests that start the real server and exercise SQS/SNS operations
/// via AWS SDK clients over HTTP.
/// </summary>
public sealed class ServerSmokeTests : IAsyncDisposable
{
    private WebApplication? _app;
    private int _port;
    private AmazonSQSClient? _sqsClient;
    private AmazonSimpleNotificationServiceClient? _snsClient;

    [Before(Test)]
    public async Task Setup()
    {
        _port = GetAvailablePort();

        var bus = new InMemoryAwsBus
        {
            ServiceUrl = new Uri($"http://localhost:{_port}")
        };

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(_port));
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        _app = builder.Build();

        var middleware = new AwsBridgeMiddleware(bus);
        _app.Map("{**path}", middleware.InvokeAsync);

        await _app.StartAsync();

        var credentials = new BasicAWSCredentials("000000000000", "fake");

        _sqsClient = new AmazonSQSClient(
            credentials,
            new AmazonSQSConfig
            {
                ServiceURL = $"http://localhost:{_port}",
                MaxErrorRetry = 0
            });

        _snsClient = new AmazonSimpleNotificationServiceClient(
            credentials,
            new AmazonSimpleNotificationServiceConfig
            {
                ServiceURL = $"http://localhost:{_port}",
                MaxErrorRetry = 0
            });
    }

    [After(Test)]
    public async Task Cleanup()
    {
        _sqsClient?.Dispose();
        _snsClient?.Dispose();
        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Cleanup();
    }

    [Test]
    [Repeat(50)]
    public async Task Sqs_SendAndReceiveMessage_ShouldWork()
    {
        var queueUrl = (await _sqsClient!.CreateQueueAsync("smoke-queue")).QueueUrl;

        queueUrl.ShouldStartWith($"http://localhost:{_port}/");

        await _sqsClient.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "Hello from integration test!"
        });

        var receiveResponse = await _sqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 1
        });

        var receivedMessage = receiveResponse.Messages.ShouldHaveSingleItem();
        receivedMessage.Body.ShouldBe("Hello from integration test!");
    }

    [Test]
    [Repeat(50)]
    public async Task Sns_PublishToSubscribedQueue_ShouldDeliverMessage()
    {
        // Create queue and topic
        var queueUrl = (await _sqsClient!.CreateQueueAsync("sns-integration-queue")).QueueUrl;
        var queueArn = (await _sqsClient.GetQueueAttributesAsync(queueUrl, ["QueueArn"]))
            .Attributes["QueueArn"];
        var topicArn = (await _snsClient!.CreateTopicAsync("sns-integration-topic")).TopicArn;

        // Subscribe queue to topic with raw delivery
        await _snsClient.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "sqs",
            Endpoint = queueArn,
            Attributes = new Dictionary<string, string> { ["RawMessageDelivery"] = "true" }
        });

        // Publish to topic
        await _snsClient.PublishAsync(topicArn, "Hello via SNS integration!");

        // Receive from queue
        var receiveResponse = await _sqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 1
        });

        var receivedMessage = receiveResponse.Messages.ShouldHaveSingleItem();
        receivedMessage.Body.ShouldBe("Hello via SNS integration!");
    }

    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

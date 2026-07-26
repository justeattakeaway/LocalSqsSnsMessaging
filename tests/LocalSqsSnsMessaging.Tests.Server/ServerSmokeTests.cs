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

        var registry = new BusRegistry("000000000000", "us-east-1", new Uri($"http://localhost:{_port}"));

        // Each test spins up its own host. The default appsettings.json sources are registered
        // with reloadOnChange:true, so every host starts two FileSystemWatchers over the output
        // directory - concurrently across the whole suite that dominates the run. These tests
        // never read configuration from disk, so turn the watchers off.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = ["--hostBuilder:reloadConfigOnChange=false"]
        });
        builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(_port));
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        _app = builder.Build();

        var middleware = new AwsBridgeMiddleware(registry);
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

    [Test]
    public async Task Sqs_CreateQueue_ShouldReturnQueueUrl()
    {
        var response = await _sqsClient!.CreateQueueAsync("test-queue");

        response.QueueUrl.ShouldNotBeNullOrWhiteSpace();
        response.QueueUrl.ShouldContain("test-queue");
        response.QueueUrl.ShouldStartWith($"http://localhost:{_port}/");
    }

    [Test]
    public async Task Sqs_GetQueueAttributes_ShouldReturnArn()
    {
        var queueUrl = (await _sqsClient!.CreateQueueAsync("attrs-queue")).QueueUrl;

        var response = await _sqsClient.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            AttributeNames = ["QueueArn"]
        });

        response.Attributes.ShouldContainKey("QueueArn");
        response.Attributes["QueueArn"].ShouldContain("attrs-queue");
    }

    [Test]
    public async Task Sns_CreateTopic_ShouldReturnTopicArn()
    {
        var response = await _snsClient!.CreateTopicAsync("test-topic");

        response.TopicArn.ShouldNotBeNullOrWhiteSpace();
        response.TopicArn.ShouldContain("test-topic");
    }

    [Test]
    public async Task Sqs_DeleteMessage_ShouldWork()
    {
        var queueUrl = (await _sqsClient!.CreateQueueAsync("delete-queue")).QueueUrl;

        await _sqsClient.SendMessageAsync(queueUrl, "message to delete");

        var receiveResponse = await _sqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 1
        });

        receiveResponse.Messages.ShouldHaveSingleItem();

        // Delete the message
        await _sqsClient.DeleteMessageAsync(queueUrl, receiveResponse.Messages[0].ReceiptHandle);

        // Should not receive any more messages
        var secondReceive = await _sqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 1,
            WaitTimeSeconds = 0
        });

        (secondReceive.Messages ?? []).ShouldBeEmpty();
    }

    [Test]
    public async Task Sqs_ListQueues_ShouldReturnCreatedQueues()
    {
        await _sqsClient!.CreateQueueAsync("list-queue-a");
        await _sqsClient.CreateQueueAsync("list-queue-b");

        var response = await _sqsClient.ListQueuesAsync(new ListQueuesRequest
        {
            QueueNamePrefix = "list-queue"
        });

        response.QueueUrls.Count.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Test]
    public async Task Sqs_MultiAccount_ShouldIsolateQueues()
    {
        // Create a second SQS client with a different 12-digit account ID
        using var sqsClient2 = new AmazonSQSClient(
            new BasicAWSCredentials("111111111111", "fake"),
            new AmazonSQSConfig
            {
                ServiceURL = $"http://localhost:{_port}",
                MaxErrorRetry = 0
            });

        // Create queues on each account
        await _sqsClient!.CreateQueueAsync("account1-queue");
        await sqsClient2.CreateQueueAsync("account2-queue");

        // Each account should only see its own queues
        var account1Queues = await _sqsClient.ListQueuesAsync(new ListQueuesRequest());
        var account2Queues = await sqsClient2.ListQueuesAsync(new ListQueuesRequest());

        account1Queues.QueueUrls.ShouldContain(q => q.Contains("account1-queue", StringComparison.Ordinal));
        account1Queues.QueueUrls.ShouldNotContain(q => q.Contains("account2-queue", StringComparison.Ordinal));

        account2Queues.QueueUrls.ShouldContain(q => q.Contains("account2-queue", StringComparison.Ordinal));
        account2Queues.QueueUrls.ShouldNotContain(q => q.Contains("account1-queue", StringComparison.Ordinal));
    }

    [Test]
    public async Task Sqs_MultiAccount_MessagesShouldBeIsolated()
    {
        using var sqsClient2 = new AmazonSQSClient(
            new BasicAWSCredentials("222222222222", "fake"),
            new AmazonSQSConfig
            {
                ServiceURL = $"http://localhost:{_port}",
                MaxErrorRetry = 0
            });

        // Create same-named queue on both accounts
        var url1 = (await _sqsClient!.CreateQueueAsync("shared-name-queue")).QueueUrl;
        var url2 = (await sqsClient2.CreateQueueAsync("shared-name-queue")).QueueUrl;

        // Send message only to account 1
        await _sqsClient.SendMessageAsync(url1, "account1 message");

        // Account 1 should receive the message
        var recv1 = await _sqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = url1,
            MaxNumberOfMessages = 1
        });
        recv1.Messages.ShouldHaveSingleItem();
        recv1.Messages[0].Body.ShouldBe("account1 message");

        // Account 2 should not receive any messages
        var recv2 = await sqsClient2.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = url2,
            MaxNumberOfMessages = 1,
            WaitTimeSeconds = 0
        });
        (recv2.Messages ?? []).ShouldBeEmpty();
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

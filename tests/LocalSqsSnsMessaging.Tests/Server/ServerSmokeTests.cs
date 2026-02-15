using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Amazon.Runtime;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using LocalSqsSnsMessaging.Http.Handlers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace LocalSqsSnsMessaging.Tests.Server;

/// <summary>
/// Integration tests that start the standalone server and exercise SQS/SNS operations
/// via real AWS SDK clients over HTTP.
/// </summary>
public sealed class ServerSmokeTests : IAsyncDisposable
{
    private WebApplication? _app;
    private int _port;
    private readonly ConcurrentDictionary<string, InMemoryAwsBus> _buses = new();
    private AmazonSQSClient? _sqsClient;
    private AmazonSimpleNotificationServiceClient? _snsClient;

    [Before(Test)]
    public async Task Setup()
    {
        _port = GetAvailablePort();

        var serviceUrl = new Uri($"http://localhost:{_port}");

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(_port));
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        _app = builder.Build();

        _app.Map("{**path}", async (HttpContext context) =>
        {
            var bus = ResolveBus(context.Request, serviceUrl);
            await HandleAwsRequest(context, bus);
        });

        await _app.StartAsync();

        _sqsClient = new AmazonSQSClient(
            new BasicAWSCredentials("000000000000", "fake"),
            new AmazonSQSConfig
            {
                ServiceURL = $"http://localhost:{_port}",
                MaxErrorRetry = 0
            });

        _snsClient = new AmazonSimpleNotificationServiceClient(
            new BasicAWSCredentials("000000000000", "fake"),
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
    public async Task Sqs_CreateQueue_ShouldReturnQueueUrl()
    {
        var response = await _sqsClient!.CreateQueueAsync("test-queue");

        response.QueueUrl.ShouldNotBeNullOrWhiteSpace();
        response.QueueUrl.ShouldContain("test-queue");
        response.QueueUrl.ShouldStartWith($"http://localhost:{_port}/");
    }

    [Test]
    public async Task Sqs_SendAndReceiveMessage_ShouldWork()
    {
        var queueUrl = (await _sqsClient!.CreateQueueAsync("send-receive-queue")).QueueUrl;

        await _sqsClient.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "Hello from server!"
        });

        var receiveResponse = await _sqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 1
        });

        receiveResponse.Messages.ShouldHaveSingleItem();
        receiveResponse.Messages[0].Body.ShouldBe("Hello from server!");
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
    public async Task Sns_PublishToSubscribedQueue_ShouldDeliverMessage()
    {
        // Create queue and topic
        var queueUrl = (await _sqsClient!.CreateQueueAsync("sns-sub-queue")).QueueUrl;
        var queueArn = (await _sqsClient.GetQueueAttributesAsync(queueUrl, ["QueueArn"]))
            .Attributes["QueueArn"];
        var topicArn = (await _snsClient!.CreateTopicAsync("sns-sub-topic")).TopicArn;

        // Subscribe queue to topic with raw delivery
        await _snsClient.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "sqs",
            Endpoint = queueArn,
            Attributes = new Dictionary<string, string> { ["RawMessageDelivery"] = "true" }
        });

        // Publish to topic
        await _snsClient.PublishAsync(topicArn, "Hello via SNS!");

        // Receive from queue
        var receiveResponse = await _sqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 1
        });

        receiveResponse.Messages.ShouldHaveSingleItem();
        receiveResponse.Messages[0].Body.ShouldBe("Hello via SNS!");
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

    /// <summary>
    /// Resolves the InMemoryAwsBus for a request based on the access key in the Authorization header.
    /// If the access key is a 12-digit number, it's treated as an account ID (like LocalStack).
    /// </summary>
    private InMemoryAwsBus ResolveBus(HttpRequest request, Uri serviceUrl)
    {
        var accountId = "000000000000";

        var authHeader = request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(authHeader))
        {
            var credIdx = authHeader.IndexOf("Credential=", StringComparison.Ordinal);
            if (credIdx >= 0)
            {
                var start = credIdx + "Credential=".Length;
                var slashIdx = authHeader.IndexOf('/', start);
                if (slashIdx > start)
                {
                    var accessKey = authHeader[start..slashIdx];
                    if (accessKey.Length == 12 && accessKey.All(char.IsAsciiDigit))
                    {
                        accountId = accessKey;
                    }
                }
            }
        }

        return _buses.GetOrAdd(accountId, id => new InMemoryAwsBus
        {
            CurrentAccountId = id,
            ServiceUrl = serviceUrl
        });
    }

    /// <summary>
    /// Handles an incoming HTTP request by bridging to the in-memory AWS operation handlers.
    /// This replicates what the server's AwsBridgeMiddleware does.
    /// </summary>
    private static async Task HandleAwsRequest(HttpContext context, InMemoryAwsBus bus)
    {
        var cancellationToken = context.RequestAborted;

        byte[] bodyBytes;
        using (var ms = new MemoryStream())
        {
            await context.Request.Body.CopyToAsync(ms, cancellationToken);
            bodyBytes = ms.ToArray();
        }

        using var requestMessage = BuildHttpRequestMessage(context.Request, bodyBytes);

        try
        {
            using var responseMessage = await RouteToHandler(context.Request, requestMessage, bodyBytes, bus, cancellationToken);
            responseMessage.Headers.Add("x-amzn-RequestId", Guid.NewGuid().ToString());
            await WriteResponse(context.Response, responseMessage);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            context.Response.StatusCode = 500;
            var errorJson = $$"""{"__type":"{{ex.GetType().Name}}","message":"{{ex.Message.Replace("\"", "\\\"", StringComparison.Ordinal)}}"}""";
            context.Response.ContentType = "application/x-amz-json-1.0";
            await context.Response.WriteAsync(errorJson, cancellationToken);
        }
    }

    private static async Task<HttpResponseMessage> RouteToHandler(
        HttpRequest request, HttpRequestMessage requestMessage, byte[] bodyBytes,
        InMemoryAwsBus bus, CancellationToken cancellationToken)
    {
        if (request.Headers.TryGetValue("x-amz-target", out var targetValues))
        {
            var target = targetValues.ToString();
            var operationName = target.Split('.')[1];
            return await SqsOperationHandler.HandleAsync(requestMessage, operationName, bus, cancellationToken);
        }

        // SNS Query protocol - extract Action from body
        var bodyString = Encoding.UTF8.GetString(bodyBytes);
        var bodyParams = System.Web.HttpUtility.ParseQueryString(bodyString);
        var action = bodyParams["Action"]
            ?? throw new InvalidOperationException("Unable to determine operation from request.");
        return await SnsOperationHandler.HandleAsync(requestMessage, action, bus, cancellationToken);
    }

    private static HttpRequestMessage BuildHttpRequestMessage(HttpRequest request, byte[] bodyBytes)
    {
        var uri = $"{request.Scheme}://{request.Host}{request.Path}{request.QueryString}";
        var requestMessage = new HttpRequestMessage(new HttpMethod(request.Method), uri);

        foreach (var header in request.Headers)
        {
            requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }

        var content = new ByteArrayContent(bodyBytes);
        foreach (var header in request.Headers)
        {
            content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }
        requestMessage.Content = content;

        return requestMessage;
    }

    private static async Task WriteResponse(HttpResponse response, HttpResponseMessage responseMessage)
    {
        response.StatusCode = (int)responseMessage.StatusCode;

        foreach (var header in responseMessage.Headers)
        {
            response.Headers[header.Key] = header.Value.ToArray();
        }

        if (responseMessage.Content != null)
        {
            foreach (var header in responseMessage.Content.Headers)
            {
                response.Headers[header.Key] = header.Value.ToArray();
            }

            await responseMessage.Content.CopyToAsync(response.Body);
        }
    }
}

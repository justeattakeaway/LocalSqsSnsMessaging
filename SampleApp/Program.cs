#pragma warning disable CA2007
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.Internal;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Amazon.Util;
using LocalSqsSnsMessaging;
using Microsoft.IO;
using SampleApp;
using Console = System.Console;

AWSConfigs.InitializeCollections = false;
AWSConfigs.CSMConfig.CSMEnabled = false;

var bus = new InMemoryAwsBus();

using var sqs = MyClients.CreateSqsClient(bus.CreateSqsClient());
//using var sqs = bus.CreateSqsClient();
using var sns = MyClients.CreateSnsClient(bus.CreateSnsClient());
//using var sns = bus.CreateSnsClient();

// Create a queue and a topic
var queueUrl = (await sqs.CreateQueueAsync("test-queue")).QueueUrl;
var topicArn = (await sns.CreateTopicAsync(new CreateTopicRequest
{
    Name = "test-topic",
    Attributes = new() { ["DisplayName"] = "Test Topic" }
})).TopicArn;
var queueArn = (await sqs.GetQueueAttributesAsync(queueUrl, ["QueueArn"])).Attributes["QueueArn"];

// Subscribe the queue to the topic
await sns.SubscribeAsync(new SubscribeRequest(topicArn, "sqs", queueArn)
{
    Attributes = new() { ["RawMessageDelivery"] = "true" }
});

// Send a message to the topic
await sns.PublishAsync(topicArn, "Hello, World!");

// Receive the message from the queue
var receiveMessageResponse = await sqs.ReceiveMessageAsync(queueUrl);
var message = receiveMessageResponse.Messages.Single();

var messageBatches =
    Enumerable.Range(0, 10_000)
        .Select(i => new PublishBatchRequestEntry { Message = $"Message number: {i}" })
        .Chunk(10);

foreach (var batch in messageBatches)
{
    await sns.PublishBatchAsync(new PublishBatchRequest
    {
        TopicArn = topicArn,
        PublishBatchRequestEntries = [.. batch]
    });
}

int messageCount = 0;

for (int i = 0; i < 1_000; i++)
{
    var messageResponse = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
    {
        QueueUrl = queueUrl,
        MaxNumberOfMessages = 10
    });

    foreach (var m in messageResponse.Messages)
    {
        if (!string.IsNullOrEmpty(m.Body))
        {
            messageCount++;
        }
    }
}

Console.WriteLine($"Received {messageCount} messages");

#pragma warning disable CA2000

internal sealed class SqsHttpRequestFactory : IHttpRequestFactory<HttpContent>
{
    private readonly IClientConfig _clientConfig;
    private readonly HttpClient _httpClient;

    internal SqsHttpRequestFactory(IAmazonSQS innerClient, IClientConfig clientConfig)
    {
        _clientConfig = clientConfig;
#pragma warning disable CA5399
        _httpClient = new HttpClient(new SqsClientHandler(innerClient));
    }

    public IHttpRequest<HttpContent> CreateHttpRequest(Uri requestUri)
    {
        return new HttpWebRequestMessage(_httpClient, requestUri, _clientConfig);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

internal sealed class SnsHttpRequestFactory : IHttpRequestFactory<HttpContent>
{
    private readonly IClientConfig _clientConfig;
    private readonly HttpClient _httpClient;

    internal SnsHttpRequestFactory(IAmazonSimpleNotificationService innerClient, IClientConfig clientConfig)
    {
        _clientConfig = clientConfig;
#pragma warning disable CA5399
        _httpClient = new HttpClient(new SnsClientHandler(innerClient));
    }

    public IHttpRequest<HttpContent> CreateHttpRequest(Uri requestUri)
    {
        return new HttpWebRequestMessage(_httpClient, requestUri, _clientConfig);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

internal sealed class StaticEndpointResolver : PipelineHandler
{
    public override Task<T> InvokeAsync<T>(IExecutionContext executionContext)
    {
        var requestContext = executionContext.RequestContext;
        requestContext.Request = requestContext.Marshaller.Marshall(requestContext.OriginalRequest);
        requestContext.Request.AuthenticationRegion = requestContext.ClientConfig.AuthenticationRegion;

        // If the request has a body and its request-specific marshaller didn't already
        // set Content-Type, follow our existing fallback logic
        if (requestContext.Request.HasRequestBody() &&
            !requestContext.Request.Headers.ContainsKey(HeaderKeys.ContentTypeHeader))
        {
            if (requestContext.Request.UseQueryString)
                requestContext.Request.Headers[HeaderKeys.ContentTypeHeader] = "application/x-amz-json-1.0";
            else
                requestContext.Request.Headers[HeaderKeys.ContentTypeHeader] = AWSSDKUtils.UrlEncodedContent;
        }

        executionContext.RequestContext.Request.Endpoint = new Uri("https://localhost:4566");
        return base.InvokeAsync<T>(executionContext);
    }
}

internal static class MemoryStreamFactory
{
    private static readonly RecyclableMemoryStreamManager Manager = new()
    {
        Settings =
        {
            ThrowExceptionOnToArray = true
        }
    };

    // static MemoryStreamFactory()
    // {
    //     Manager.UsageReport += (sender, args) =>
    //     {
    //         Console.WriteLine("Stream usage report:");
    //         Console.WriteLine($"  Small pool in use: {args.SmallPoolInUseBytes}");
    //         Console.WriteLine($"  Small pool free: {args.SmallPoolFreeBytes}");
    //     };
    // }

    public static RecyclableMemoryStream GetStream(string? tag = null)
        => Manager.GetStream(tag);

    public static RecyclableMemoryStream GetStream(int requiredSize, string? tag = null)
        => Manager.GetStream(tag, requiredSize);
}

internal sealed class PooledJsonContent<T> : HttpContent
{
    private readonly T _value;
    private readonly JsonTypeInfo<T> _jsonTypeInfo;
    private RecyclableMemoryStream? _pooledStream;

    public PooledJsonContent(T value, JsonTypeInfo<T> jsonTypeInfo)
    {
        _value = value;
        _jsonTypeInfo = jsonTypeInfo;
        Headers.ContentType = new MediaTypeHeaderValue("application/json");
    }

    protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
    {
        _pooledStream = MemoryStreamFactory.GetStream(typeof(T).Name);
        await JsonSerializer.SerializeAsync(_pooledStream, _value, _jsonTypeInfo, cancellationToken);
        _pooledStream.Position = 0;
        return _pooledStream;
    }
    
    protected override void Dispose(bool disposing)
    {
        if (disposing && _pooledStream != null)
        {
            _pooledStream.Dispose();
            _pooledStream = null;
        }
        base.Dispose(disposing);
    }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        await using var ms = await CreateContentReadStreamAsync(CancellationToken.None);
        await ms.CopyToAsync(stream);
    }

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}
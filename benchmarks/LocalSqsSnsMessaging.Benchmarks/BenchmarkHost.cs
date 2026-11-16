extern alias server;

using Amazon.DynamoDBv2;
using Amazon.EventBridge;
using Amazon.Runtime;
using Amazon.SimpleNotificationService;
using Amazon.SQS;
using LocalSqsSnsMessaging.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using ServerBusRegistry = server::LocalSqsSnsMessaging.Server.BusRegistry;
using ServerMiddleware = server::LocalSqsSnsMessaging.Server.AwsBridgeMiddleware;

namespace LocalSqsSnsMessaging.Benchmarks;

/// <summary>Where a benchmark's requests are served.</summary>
public enum Host
{
    /// <summary>The library: SDK clients over <see cref="InMemoryAwsHttpMessageHandler"/>.</summary>
    Library,

    /// <summary>The server's middleware, on an in-memory <see cref="TestServer"/>.</summary>
    Server,
}

/// <summary>
/// The SDK clients, and a raw HTTP client for the SQS Query protocol (which the v4 SDK no longer
/// speaks), for one <see cref="Host"/>.
/// </summary>
internal sealed class BenchmarkHost : IAsyncDisposable
{
    private const string AccountId = "000000000000";
    private const string Region = "us-east-1";

    private readonly IAsyncDisposable? _app;

    private BenchmarkHost(
        AmazonSQSClient sqs,
        AmazonSimpleNotificationServiceClient sns,
        AmazonEventBridgeClient eventBridge,
        AmazonDynamoDBClient dynamoDb,
        HttpClient sqsQuery,
        IAsyncDisposable? app)
    {
        Sqs = sqs;
        Sns = sns;
        EventBridge = eventBridge;
        DynamoDb = dynamoDb;
        SqsQuery = sqsQuery;
        _app = app;
    }

    public AmazonSQSClient Sqs { get; }

    public AmazonSimpleNotificationServiceClient Sns { get; }

    public AmazonEventBridgeClient EventBridge { get; }

    public AmazonDynamoDBClient DynamoDb { get; }

    /// <summary>Posts form-encoded SQS Query requests; its base address is the SQS endpoint.</summary>
    public HttpClient SqsQuery { get; }

    public static async Task<BenchmarkHost> StartAsync(Host host)
    {
        if (host == Host.Library)
        {
            var bus = new InMemoryAwsBus();
            var sqsQuery = new HttpClient(new InMemoryAwsHttpMessageHandler(bus, AwsServiceType.Sqs), disposeHandler: true)
            {
                BaseAddress = new Uri($"https://sqs.{Region}.amazonaws.com/"),
            };
            return new BenchmarkHost(
                bus.CreateSqsClient(), bus.CreateSnsClient(), bus.CreateEventBridgeClient(), bus.CreateDynamoDbClient(), sqsQuery, app: null);
        }

        var serviceUrl = new Uri("http://localhost/");
        var registry = new ServerBusRegistry(AccountId, Region, serviceUrl);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = ["--hostBuilder:reloadConfigOnChange=false"] });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Map("{**path}", new ServerMiddleware(registry).InvokeAsync);
        await app.StartAsync();

        var handler = new CanonicalUriHandler(app.GetTestServer().CreateHandler());
        var factory = new HandlerHttpClientFactory(handler);
        var credentials = new BasicAWSCredentials(AccountId, "fake");
        return new BenchmarkHost(
            new AmazonSQSClient(credentials, Configure(new AmazonSQSConfig(), serviceUrl, factory)),
            new AmazonSimpleNotificationServiceClient(credentials, Configure(new AmazonSimpleNotificationServiceConfig(), serviceUrl, factory)),
            new AmazonEventBridgeClient(credentials, Configure(new AmazonEventBridgeConfig(), serviceUrl, factory)),
            new AmazonDynamoDBClient(credentials, Configure(new AmazonDynamoDBConfig(), serviceUrl, factory)),
            new HttpClient(handler, disposeHandler: false) { BaseAddress = serviceUrl },
            app);
    }

    private static T Configure<T>(T config, Uri serviceUrl, HttpClientFactory factory)
        where T : ClientConfig
    {
        config.ServiceURL = serviceUrl.ToString();
        config.AuthenticationRegion = Region;
        config.MaxErrorRetry = 0;
        config.HttpClientFactory = factory;
        return config;
    }

    public async ValueTask DisposeAsync()
    {
        Sqs.Dispose();
        Sns.Dispose();
        EventBridge.Dispose();
        DynamoDb.Dispose();
        SqsQuery.Dispose();
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    /// <summary>
    /// The v4 SDK builds its request URIs with <c>DangerousDisablePathAndQueryCanonicalization</c>,
    /// whose path <see cref="TestServer"/> cannot read; give it the same URI, built the ordinary way.
    /// </summary>
    private sealed class CanonicalUriHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.RequestUri = new Uri(request.RequestUri!.OriginalString);
            return base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>Hands the SDK an <see cref="HttpClient"/> over one handler, as the library does.</summary>
    private sealed class HandlerHttpClientFactory(HttpMessageHandler handler) : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig) => new(handler, disposeHandler: false);

        public override string GetConfigUniqueString(IClientConfig clientConfig) => "Benchmark";

        public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => false;

        // Each benchmark case starts its own server: never hand it a client cached for an earlier one.
        public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;
    }
}

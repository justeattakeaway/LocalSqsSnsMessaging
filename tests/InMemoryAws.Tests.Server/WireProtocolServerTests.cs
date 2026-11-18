using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.Runtime;
using InMemoryAws.Http;
using InMemoryAws.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace InMemoryAws.Tests.Server;

/// <summary>
/// The server's adapter onto each wire protocol: that the status, headers and body a protocol
/// answers with reach the client intact over real HTTP. What each protocol answers is tested
/// through the library, which calls the same wire protocols.
/// </summary>
public sealed class WireProtocolServerTests : IAsyncDisposable
{
    private const string AccountId = "000000000000";

    private WebApplication? _app;
    private int _port;
    private HttpClient? _http;

    [Before(Test)]
    public async Task Setup()
    {
        _port = GetAvailablePort();
        var registry = new CloudRegistry(AccountId, "us-east-1", new Uri($"http://localhost:{_port}"));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = ["--hostBuilder:reloadConfigOnChange=false"] });
        builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(_port));
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        _app = builder.Build();
        _app.Map("{**path}", new AwsBridgeMiddleware(registry).InvokeAsync);
        await _app.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://localhost:{_port}/") };
    }

    [After(Test)]
    public async Task Cleanup()
    {
        _http?.Dispose();
        _http = null;
        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            _app = null;
        }
    }

    public async ValueTask DisposeAsync() => await Cleanup();

    [Test]
    public async Task EventBridge_IsServedThroughTheSdk()
    {
        using var eventBridge = new AmazonEventBridgeClient(
            new BasicAWSCredentials(AccountId, "fake"),
            new AmazonEventBridgeConfig { ServiceURL = $"http://localhost:{_port}", AuthenticationRegion = "us-east-1", MaxErrorRetry = 0 });

        await eventBridge.PutRuleAsync(new PutRuleRequest { Name = "orders", EventPattern = """{"source":["my.orders"]}""" });
        var rules = await eventBridge.ListRulesAsync(new ListRulesRequest());
        var put = await eventBridge.PutEventsAsync(new PutEventsRequest
        {
            Entries = [new PutEventsRequestEntry { Source = "my.orders", DetailType = "OrderPlaced", Detail = "{}" }],
        });
        var missing = await Should.ThrowAsync<Amazon.EventBridge.Model.ResourceNotFoundException>(() =>
            eventBridge.DescribeRuleAsync(new DescribeRuleRequest { Name = "no-such-rule" }));

        rules.Rules.ShouldHaveSingleItem().Name.ShouldBe("orders");
        put.FailedEntryCount.ShouldBe(0);
        missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task EventBridge_Refusal_KeepsItsStatusContentTypeAndRequestId()
    {
        using var response = await PostAsync("application/x-amz-json-1.1", "{}", "AWSEvents.NoSuchOperation");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/x-amz-json-1.1");
        response.Headers.GetValues("x-amzn-RequestId").ShouldHaveSingleItem().ShouldNotBeNullOrEmpty();
        (await response.Content.ReadAsStringAsync()).ShouldBe("""{"__type":"com.amazon.coral.service#UnknownOperationException"}""");
    }

    [Test]
    public async Task SqsJson_Error_KeepsItsQueryErrorHeader()
    {
        using var response = await PostAsync(
            "application/x-amz-json-1.0", """{"QueueUrl":"http://localhost/000000000000/no-such-queue"}""", "AmazonSQS.GetQueueAttributes");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Headers.GetValues("x-amzn-query-error").ShouldHaveSingleItem().ShouldBe("AWS.SimpleQueueService.NonExistentQueue;Sender");
        using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        error.RootElement.GetProperty("__type").GetString().ShouldBe("com.amazonaws.sqs#QueueDoesNotExist");
    }

    [Test]
    public async Task Sns_Error_CarriesTheRequestIdOfItsHeader()
    {
        using var response = await PostAsync(
            "application/x-www-form-urlencoded",
            $"Action=GetTopicAttributes&Version=2010-03-31&TopicArn={WebUtility.UrlEncode($"arn:aws:sns:us-east-1:{AccountId}:no-such-topic")}",
            target: null);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/xml");
        XNamespace ns = "http://sns.amazonaws.com/doc/2010-03-31/";
        var root = XElement.Parse(await response.Content.ReadAsStringAsync());
        root.Element(ns + "RequestId")!.Value.ShouldBe(response.Headers.GetValues("x-amzn-RequestId").ShouldHaveSingleItem());
    }

    [Test]
    public async Task DynamoDb_Response_CarriesTheCrc32OfItsBody()
    {
        using var response = await PostAsync("application/x-amz-json-1.0", "{}", "DynamoDB_20120810.ListTables");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsByteArrayAsync();
        response.Headers.GetValues("x-amz-crc32").ShouldHaveSingleItem()
            .ShouldBe(Crc32.Compute(body).ToString(CultureInfo.InvariantCulture));
    }

    private async Task<HttpResponseMessage> PostAsync(string contentType, string body, string? target)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/", UriKind.Relative));
        request.Content = new StringContent(body, Encoding.UTF8);
        request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
        if (target is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Amz-Target", target);
        }

        return await _http!.SendAsync(request);
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

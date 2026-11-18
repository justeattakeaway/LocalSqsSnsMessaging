using System.Net;
using System.Text;
using System.Xml.Linq;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using InMemoryAws.Http;
using Shouldly;

namespace InMemoryAws.Tests.Http;

/// <summary>
/// The exact answers each wire protocol gives a request it cannot run, through
/// <see cref="InMemoryAwsHttpMessageHandler"/>. The server answers through the same wire
/// protocols; <c>WireProtocolRefusalTests</c> checks the shape of these answers against Floci.
/// </summary>
public sealed class WireProtocolTests : IDisposable
{
    private static readonly XNamespace SnsNs = "http://sns.amazonaws.com/doc/2010-03-31/";

    private readonly InMemoryAwsCloud _cloud = new();
    private readonly Dictionary<AwsServiceType, HttpClient> _clients = [];

    public void Dispose()
    {
        foreach (var client in _clients.Values)
        {
            client.Dispose();
        }
    }

    [Test]
    public async Task EventBridge_OperationItDoesNotHave_IsAnUnknownOperationException()
    {
        using var response = await PostAsync(AwsServiceType.EventBridge, "application/x-amz-json-1.1", "{}", "AWSEvents.NoSuchOperation");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldBe("""{"__type":"com.amazon.coral.service#UnknownOperationException"}""");
        response.Headers.GetValues("x-amzn-RequestId").ShouldHaveSingleItem().ShouldNotBeNullOrEmpty();
    }

    [Test]
    [Arguments("{not json")]
    [Arguments("""{"Limit":"ten"}""")]
    public async Task EventBridge_BodyThatIsNotJsonOrHasAValueOfTheWrongType_IsASerializationException(string body)
    {
        using var response = await PostAsync(AwsServiceType.EventBridge, "application/x-amz-json-1.1", body, "AWSEvents.ListRules");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/x-amz-json-1.1");
        (await response.Content.ReadAsStringAsync()).ShouldBe("""{"__type":"com.amazon.coral.service#SerializationException"}""");
    }

    [Test]
    public async Task Sqs_JsonBodyThatIsNotJson_IsASerializationExceptionWithItsQueryErrorCode()
    {
        using var response = await PostAsync(AwsServiceType.Sqs, "application/x-amz-json-1.0", "{not json", "AmazonSQS.ListQueues");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldBe("""{"__type":"com.amazon.coral.service#SerializationException"}""");
        response.Headers.GetValues("x-amzn-query-error").ShouldHaveSingleItem().ShouldBe("SerializationException;Sender");
    }

    [Test]
    public async Task Sns_ActionItDoesNotHave_IsAnInvalidAction()
    {
        using var response = await PostAsync(AwsServiceType.Sns, "application/x-www-form-urlencoded", "Action=NoSuchAction&Version=2010-03-31", target: null);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var error = XElement.Parse(await response.Content.ReadAsStringAsync()).Element(SnsNs + "Error")!;
        error.Element(SnsNs + "Type")!.Value.ShouldBe("Sender");
        error.Element(SnsNs + "Code")!.Value.ShouldBe("InvalidAction");
        error.Element(SnsNs + "Message")!.Value.ShouldBe("The action NoSuchAction is not valid for this endpoint.");
    }

    [Test]
    public async Task Sns_Error_CarriesTheRequestIdOfItsHeader()
    {
        var topicArn = $"arn:aws:sns:{_cloud.CurrentRegion}:{_cloud.CurrentAccountId}:no-such-topic";
        using var response = await PostAsync(
            AwsServiceType.Sns, "application/x-www-form-urlencoded", $"Action=GetTopicAttributes&Version=2010-03-31&TopicArn={WebUtility.UrlEncode(topicArn)}", target: null);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var root = XElement.Parse(await response.Content.ReadAsStringAsync());
        root.Element(SnsNs + "Error")!.Element(SnsNs + "Code")!.Value.ShouldBe("NotFound");
        root.Element(SnsNs + "RequestId")!.Value.ShouldBe(response.Headers.GetValues("x-amzn-RequestId").ShouldHaveSingleItem());
    }

    [Test]
    public async Task EventBridge_ErrorMessageQuotingQuotesBackslashesAndNewlines_ReachesTheSdkIntact()
    {
        using var eventBridge = _cloud.CreateEventBridgeClient();
        // Not a constant, which the SDK's analyzer would refuse to compile.
        var name = string.Concat("bad\"name", "\\with\nbreaks");

        var error = await Should.ThrowAsync<AmazonEventBridgeException>(() =>
            eventBridge.CreateEventBusAsync(new CreateEventBusRequest { Name = name }));

        error.ErrorCode.ShouldBe("ValidationException");
        error.Message.ShouldContain($"Value '{name}' at 'name'");
    }

    private async Task<HttpResponseMessage> PostAsync(AwsServiceType service, string contentType, string body, string? target)
    {
        if (!_clients.TryGetValue(service, out var client))
        {
#pragma warning disable CA2000 // The HttpClient owns the handler.
            client = new HttpClient(new InMemoryAwsHttpMessageHandler(_cloud, service))
#pragma warning restore CA2000
            {
                BaseAddress = new Uri($"https://{(service == AwsServiceType.EventBridge ? "events" : service == AwsServiceType.Sns ? "sns" : "sqs")}.{_cloud.CurrentRegion}.amazonaws.com/"),
            };
            _clients[service] = client;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/", UriKind.Relative));
        request.Content = new StringContent(body, Encoding.UTF8);
        request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
        if (target is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Amz-Target", target);
        }

        return await client.SendAsync(request);
    }
}

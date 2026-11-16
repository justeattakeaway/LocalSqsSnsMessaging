using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Shouldly;

namespace LocalSqsSnsMessaging.Tests;

/// <summary>
/// How each wire protocol refuses a request it cannot run: with HTTP 400 and an error in its own
/// format, never a generic server error. Raw HTTP requests, run against the in-memory library and,
/// via the verification project, Floci. These pin only what Floci and AWS agree on; the exact codes
/// and messages the emulator answers with are pinned in memory (<c>Http/WireProtocolTests</c>).
/// </summary>
public abstract class WireProtocolRefusalTests : WaitingTestBase
{
    protected string AccountId = null!;

    /// <summary>An HTTP client whose base address serves <paramref name="service"/> (<c>sqs</c>, <c>sns</c> or <c>events</c>).</summary>
    protected abstract HttpClient ClientFor(string service);

    [Test]
    public async Task EventBridge_OperationItDoesNotHave_IsRefusedWithAJsonError(CancellationToken cancellationToken)
    {
        var (response, body) = await PostAsync("events", "application/x-amz-json-1.1", "{}", "AWSEvents.NoSuchOperation", cancellationToken);
        using (response)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            response.Content.Headers.ContentType!.MediaType.ShouldBe("application/x-amz-json-1.1");
            ErrorType(body).ShouldNotBeNullOrEmpty();
        }
    }

    [Test]
    public async Task EventBridge_BodyThatIsNotJson_IsASerializationException(CancellationToken cancellationToken)
    {
        var (response, body) = await PostAsync("events", "application/x-amz-json-1.1", "{not json", "AWSEvents.ListRules", cancellationToken);
        using (response)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            ErrorType(body).ShouldEndWith("SerializationException");
        }
    }

    [Test]
    public async Task Sqs_JsonBodyThatIsNotJson_IsASerializationException(CancellationToken cancellationToken)
    {
        var (response, body) = await PostAsync("sqs", "application/x-amz-json-1.0", "{not json", "AmazonSQS.ListQueues", cancellationToken);
        using (response)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            ErrorType(body).ShouldEndWith("SerializationException");
            response.Headers.GetValues("x-amzn-query-error").ShouldHaveSingleItem().ShouldStartWith("SerializationException;Sender");
        }
    }

    [Test]
    public async Task Sns_RequestWithoutAnAction_IsRefusedAsMissingAction(CancellationToken cancellationToken)
    {
        var (response, body) = await PostAsync("sns", "application/x-www-form-urlencoded", "Version=2010-03-31", target: null, cancellationToken);
        using (response)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            var error = QueryError(body);
            error.Type.ShouldBe("Sender");
            error.Code.ShouldBe("MissingAction");
        }
    }

    [Test]
    public async Task Sns_ActionItDoesNotHave_IsRefusedWithAnXmlError(CancellationToken cancellationToken)
    {
        var (response, body) = await PostAsync("sns", "application/x-www-form-urlencoded", "Action=NoSuchAction&Version=2010-03-31", target: null, cancellationToken);
        using (response)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            var error = QueryError(body);
            error.Type.ShouldBe("Sender");
            error.Code.ShouldNotBeNullOrEmpty();
        }
    }

    private async Task<(HttpResponseMessage Response, string Body)> PostAsync(
        string service, string contentType, string body, string? target, CancellationToken cancellationToken)
    {
        // Real AWS refuses the placeholder signature before it reads the request.
        Skip.When(IsRealAwsMode, "Raw requests carry a placeholder signature, which AWS refuses first");

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/", UriKind.Relative));
        request.Content = new StringContent(body, Encoding.UTF8);
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        if (target is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Amz-Target", target);
        }

        // The account and region come from the credential scope, as from a signed request.
        request.Headers.TryAddWithoutValidation(
            "Authorization",
            $"AWS4-HMAC-SHA256 Credential={AccountId}/20260101/us-east-1/{service}/aws4_request, SignedHeaders=host, Signature=0000");

        var response = await ClientFor(service).SendAsync(request, cancellationToken);
        return (response, await response.Content.ReadAsStringAsync(cancellationToken));
    }

    /// <summary>The <c>__type</c> of a JSON error document.</summary>
    private static string? ErrorType(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("__type").GetString();
    }

    /// <summary>The <c>Type</c> and <c>Code</c> of a Query <c>ErrorResponse</c>, whatever its namespace.</summary>
    private static (string Type, string Code) QueryError(string body)
    {
        var root = XElement.Parse(body);
        root.Name.LocalName.ShouldBe("ErrorResponse");
        var error = root.Elements().Single(e => e.Name.LocalName == "Error");
        return (
            error.Elements().Single(e => e.Name.LocalName == "Type").Value,
            error.Elements().Single(e => e.Name.LocalName == "Code").Value);
    }
}

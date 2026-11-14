using System.Net;
using System.Text;
using System.Xml.Linq;
using Amazon.SQS;
using Amazon.SQS.Model;
using LocalSqsSnsMessaging.Http;
using Shouldly;

namespace LocalSqsSnsMessaging.Tests.Http;

/// <summary>
/// SQS over the AWS Query protocol (form-encoded <c>Action=...</c> requests, XML responses), and
/// how SQS refuses a raw request that names no protocol, through <see cref="InMemoryAwsHttpMessageHandler"/>.
/// The evaluation suite covers single sends and receives; these cover the flattened lists and maps
/// of the batch, attribute and tag operations, and the transport-level refusals.
/// </summary>
public sealed class SqsQueryProtocolTests : IDisposable
{
    private static readonly XNamespace Ns = "http://queue.amazonaws.com/doc/2012-11-05/";

    private readonly InMemoryAwsBus _bus = new();
    private readonly AmazonSQSClient _sqs;
    private readonly HttpClient _http;

    public SqsQueryProtocolTests()
    {
        _sqs = _bus.CreateSqsClient();
#pragma warning disable CA2000 // The HttpClient owns the handler.
        _http = new HttpClient(new InMemoryAwsHttpMessageHandler(_bus, AwsServiceType.Sqs))
#pragma warning restore CA2000
        {
            BaseAddress = new Uri($"https://sqs.{_bus.CurrentRegion}.amazonaws.com/"),
        };
    }

    public void Dispose()
    {
        _sqs.Dispose();
        _http.Dispose();
    }

    private async Task<(HttpStatusCode Status, XElement Root)> QueryAsync(params (string Key, string Value)[] parameters)
    {
        var body = string.Join("&", parameters.Append((Key: "Version", Value: "2012-11-05"))
            .Select(p => $"{WebUtility.UrlEncode(p.Key)}={WebUtility.UrlEncode(p.Value)}"));
        using var content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");
        using var response = await _http.PostAsync(new Uri("/", UriKind.Relative), content);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/xml");
        return (response.StatusCode, XElement.Parse(await response.Content.ReadAsStringAsync()));
    }

    private async Task<(HttpStatusCode Status, string Body)> RawPostAsync(string body, string contentType, string? target)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/", UriKind.Relative));
        request.Content = new StringContent(body, Encoding.UTF8);
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        if (target is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Amz-Target", target);
        }

        using var response = await _http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Test]
    public async Task CreateQueue_AttributesAndTagsSentOverQuery_ReadBackOverBothProtocols()
    {
        var (status, root) = await QueryAsync(
            ("Action", "CreateQueue"),
            ("QueueName", "query-create"),
            ("Attribute.1.Name", "VisibilityTimeout"),
            ("Attribute.1.Value", "45"),
            ("Attribute.2.Name", "DelaySeconds"),
            ("Attribute.2.Value", "3"),
            ("Tag.1.Key", "team"),
            ("Tag.1.Value", "blue"));
        status.ShouldBe(HttpStatusCode.OK);
        root.Name.ShouldBe(Ns + "CreateQueueResponse");
        var url = root.Element(Ns + "CreateQueueResult")!.Element(Ns + "QueueUrl")!.Value;

        var attributes = (await _sqs.GetQueueAttributesAsync(url, ["VisibilityTimeout", "DelaySeconds"])).Attributes;
        attributes["VisibilityTimeout"].ShouldBe("45");
        attributes["DelaySeconds"].ShouldBe("3");
        (await _sqs.ListQueueTagsAsync(new ListQueueTagsRequest { QueueUrl = url })).Tags["team"].ShouldBe("blue");

        (status, root) = await QueryAsync(("Action", "GetQueueAttributes"), ("QueueUrl", url), ("AttributeName.1", "VisibilityTimeout"), ("AttributeName.2", "DelaySeconds"));
        status.ShouldBe(HttpStatusCode.OK);
        root.Descendants(Ns + "Attribute")
            .ToDictionary(a => a.Element(Ns + "Name")!.Value, a => a.Element(Ns + "Value")!.Value)
            .ShouldBe(new Dictionary<string, string> { ["VisibilityTimeout"] = "45", ["DelaySeconds"] = "3" }, ignoreOrder: true);

        (status, root) = await QueryAsync(("Action", "ListQueueTags"), ("QueueUrl", url));
        status.ShouldBe(HttpStatusCode.OK);
        var tag = root.Descendants(Ns + "Tag").ShouldHaveSingleItem();
        (tag.Element(Ns + "Key")!.Value, tag.Element(Ns + "Value")!.Value).ShouldBe(("team", "blue"));

        (status, root) = await QueryAsync(("Action", "ListQueues"), ("QueueNamePrefix", "query-"));
        status.ShouldBe(HttpStatusCode.OK);
        root.Descendants(Ns + "QueueUrl").Select(e => e.Value).ShouldBe([url]);
    }

    [Test]
    public async Task Batches_SentAndDeletedOverQuery_CarryEveryEntryAndAttribute()
    {
        var url = (await _sqs.CreateQueueAsync("query-batch")).QueueUrl;
        var bytes = new byte[] { 0, 1, 2, 250 };

        var (status, root) = await QueryAsync(
            ("Action", "SendMessageBatch"),
            ("QueueUrl", url),
            ("SendMessageBatchRequestEntry.1.Id", "a"),
            ("SendMessageBatchRequestEntry.1.MessageBody", "first"),
            ("SendMessageBatchRequestEntry.1.MessageAttribute.1.Name", "blob"),
            ("SendMessageBatchRequestEntry.1.MessageAttribute.1.Value.DataType", "Binary"),
            ("SendMessageBatchRequestEntry.1.MessageAttribute.1.Value.BinaryValue", Convert.ToBase64String(bytes)),
            ("SendMessageBatchRequestEntry.2.Id", "b"),
            ("SendMessageBatchRequestEntry.2.MessageBody", "second"),
            ("SendMessageBatchRequestEntry.2.DelaySeconds", "0"));
        status.ShouldBe(HttpStatusCode.OK);
        root.Descendants(Ns + "SendMessageBatchResultEntry").Select(e => e.Element(Ns + "Id")!.Value).ShouldBe(["a", "b"], ignoreOrder: true);
        root.Descendants(Ns + "BatchResultErrorEntry").ShouldBeEmpty();

        // The binary attribute sent over Query reads back byte for byte over JSON...
        var viaJson = (await _sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = url,
            MaxNumberOfMessages = 10,
            MessageAttributeNames = ["All"],
            VisibilityTimeout = 0,
        })).Messages;
        viaJson.Count.ShouldBe(2);
        viaJson.Single(m => m.Body == "first").MessageAttributes["blob"].BinaryValue.ToArray().ShouldBe(bytes);

        // ...and over Query, with the system attributes asked for by name.
        (status, root) = await QueryAsync(
            ("Action", "ReceiveMessage"),
            ("QueueUrl", url),
            ("MaxNumberOfMessages", "10"),
            ("AttributeName.1", "All"),
            ("MessageAttributeName.1", "All"));
        status.ShouldBe(HttpStatusCode.OK);
        var messages = root.Descendants(Ns + "Message").ToList();
        messages.Count.ShouldBe(2);
        var first = messages.Single(m => m.Element(Ns + "Body")!.Value == "first");
        first.Elements(Ns + "Attribute").Select(a => a.Element(Ns + "Name")!.Value).ShouldContain("SentTimestamp");
        var blob = first.Elements(Ns + "MessageAttribute").ShouldHaveSingleItem();
        blob.Element(Ns + "Name")!.Value.ShouldBe("blob");
        blob.Element(Ns + "Value")!.Element(Ns + "DataType")!.Value.ShouldBe("Binary");
        Convert.FromBase64String(blob.Element(Ns + "Value")!.Element(Ns + "BinaryValue")!.Value).ShouldBe(bytes);

        var handles = messages.Select(m => m.Element(Ns + "ReceiptHandle")!.Value).ToList();
        (status, root) = await QueryAsync(
            ("Action", "DeleteMessageBatch"),
            ("QueueUrl", url),
            ("DeleteMessageBatchRequestEntry.1.Id", "x"),
            ("DeleteMessageBatchRequestEntry.1.ReceiptHandle", handles[0]),
            ("DeleteMessageBatchRequestEntry.2.Id", "y"),
            ("DeleteMessageBatchRequestEntry.2.ReceiptHandle", handles[1]));
        status.ShouldBe(HttpStatusCode.OK);
        root.Descendants(Ns + "DeleteMessageBatchResultEntry").Select(e => e.Element(Ns + "Id")!.Value).ShouldBe(["x", "y"], ignoreOrder: true);

        var left = (await _sqs.GetQueueAttributesAsync(url, ["ApproximateNumberOfMessages", "ApproximateNumberOfMessagesNotVisible"])).Attributes;
        left.Values.ShouldAllBe(v => v == "0");
    }

    [Test]
    public async Task ReceiveMessage_OverQuery_KeepsCarriageReturnsInTheBodyItsMd5Describes()
    {
        var url = (await _sqs.CreateQueueAsync("query-carriage-return")).QueueUrl;
        const string Body = "a\rb\r\nc";
        var sent = await _sqs.SendMessageAsync(url, Body);

        var (status, root) = await QueryAsync(("Action", "ReceiveMessage"), ("QueueUrl", url));

        status.ShouldBe(HttpStatusCode.OK);
        var message = root.Descendants(Ns + "Message").ShouldHaveSingleItem();
        message.Element(Ns + "Body")!.Value.ShouldBe(Body);
        message.Element(Ns + "MD5OfBody")!.Value.ShouldBe(sent.MD5OfMessageBody);
    }

    [Test]
    public async Task Get_WithTheQueryParametersInTheUrl_IsServedLikeAPost()
    {
        var url = (await _sqs.CreateQueueAsync("query-get")).QueueUrl;

        using var response = await _http.GetAsync(new Uri("/?Action=GetQueueUrl&QueueName=query-get&Version=2012-11-05", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var root = XElement.Parse(await response.Content.ReadAsStringAsync());
        root.Descendants(Ns + "QueueUrl").Single().Value.ShouldBe(url);
    }

    [Test]
    public async Task Query_WithoutAnAction_IsAnXmlMissingActionError()
    {
        var (status, root) = await QueryAsync(("QueueName", "anything"));

        status.ShouldBe(HttpStatusCode.BadRequest);
        root.Name.ShouldBe(Ns + "ErrorResponse");
        var error = root.Element(Ns + "Error")!;
        error.Element(Ns + "Type")!.Value.ShouldBe("Sender");
        error.Element(Ns + "Code")!.Value.ShouldBe("MissingAction");
    }

    [Test]
    public async Task Json_WithATargetSqsDoesNotHave_IsRefusedAsAnUnknownOperation()
    {
        var (status, body) = await RawPostAsync("{}", "application/x-amz-json-1.0", "AmazonSQS.NoSuchOperation");

        status.ShouldBe(HttpStatusCode.BadRequest);
        body.ShouldBe("""{"__type":"com.amazon.coral.service#UnknownOperationException"}""");
    }

    [Test]
    public async Task Json_WithNeitherProtocolsContentType_IsRefusedAsAnUnknownOperation()
    {
        var (status, body) = await RawPostAsync("{}", "text/plain", target: null);

        status.ShouldBe(HttpStatusCode.NotFound);
        body.ShouldBe("<UnknownOperationException/>\n");
    }

    [Test]
    [Arguments("<")]
    [Arguments("é")]
    [Arguments("中")]
    public async Task MaximumSizeMessage_ThatTheSdkEscapesToSeveralMiB_IsNotRefusedAsTooLarge(string character)
    {
        // The AWS SDK for .NET writes '<' and every non-ASCII character as a six-byte \uXXXX
        // escape, so these 1 MiB messages take 3 to 6 MiB on the wire; the 413 limit is on the
        // content, not its escaping.
        var url = (await _sqs.CreateQueueAsync("query-escaped")).QueueUrl;
        var body = string.Concat(Enumerable.Repeat(character, 1048576 / Encoding.UTF8.GetByteCount(character)));

        await _sqs.SendMessageAsync(url, body);

        var received = (await _sqs.ReceiveMessageAsync(url)).Messages.ShouldHaveSingleItem();
        received.Body.ShouldBe(body);
    }

    [Test]
    public async Task Query_OverTwoMiB_IsRefusedWith413BeforeSqsValidation()
    {
        var url = (await _sqs.CreateQueueAsync("query-too-large")).QueueUrl;
        var body = $"Action=SendMessage&Version=2012-11-05&QueueUrl={WebUtility.UrlEncode(url)}&MessageBody={new string('x', 2 * 1024 * 1024)}";
        using var content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");

        using var response = await _http.PostAsync(new Uri("/", UriKind.Relative), content);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        ((await _sqs.ReceiveMessageAsync(url)).Messages ?? []).ShouldBeEmpty();
    }
}

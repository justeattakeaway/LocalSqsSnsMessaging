#if !ASPNETCORE
using System.Buffers;
using System.Net;
using System.Text;
#if NETSTANDARD2_0
using InMemoryAws.Http.Internal;
#endif

namespace InMemoryAws.Http;

/// <summary>
/// HTTP message handler that intercepts AWS SDK HTTP requests and routes them to in-memory implementations.
/// </summary>
/// <remarks>
/// One handler speaks one service's protocol. Projects that reference the AWS SDK get
/// <c>CreateSqsClient()</c> and the other <c>Create...Client()</c> methods, and
/// <c>InMemoryAwsSdkHttpClientFactory</c>, which use these handlers; to wire a client by hand, return an
/// <see cref="HttpClient"/> over this handler from an AWS SDK <c>Amazon.Runtime.HttpClientFactory</c>
/// (see <see cref="AwsServiceEndpoints"/>).
/// </remarks>
public sealed class InMemoryAwsHttpMessageHandler : DelegatingHandler
{
    private readonly InMemoryAwsCloud _cloud;
    private readonly AwsServiceType _serviceType;

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryAwsHttpMessageHandler"/> class.
    /// </summary>
    /// <param name="cloud">The in-memory AWS cloud instance that will handle the intercepted requests.</param>
    /// <param name="serviceType">The AWS service whose protocol this handler should speak.</param>
    public InMemoryAwsHttpMessageHandler(InMemoryAwsCloud cloud, AwsServiceType serviceType)
    {
        ArgumentNullException.ThrowIfNull(cloud);
        _cloud = cloud;
        _serviceType = serviceType is AwsServiceType.Sqs or AwsServiceType.Sns or AwsServiceType.EventBridge
            or AwsServiceType.DynamoDb or AwsServiceType.DynamoDbStreams or AwsServiceType.S3
            ? serviceType
            : throw new NotSupportedException($"Service type {serviceType} is not supported.");
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_serviceType == AwsServiceType.Sqs)
        {
            return await SendSqsAsync(request, cancellationToken).ConfigureAwait(false);
        }

        if (_serviceType is AwsServiceType.DynamoDb or AwsServiceType.DynamoDbStreams)
        {
            return await SendDynamoDbAsync(request, cancellationToken).ConfigureAwait(false);
        }

        if (_serviceType == AwsServiceType.S3)
        {
            return await SendS3Async(request, cancellationToken).ConfigureAwait(false);
        }

        if (_serviceType == AwsServiceType.Sns)
        {
            return await SendSnsAsync(request, cancellationToken).ConfigureAwait(false);
        }

        return await SendEventBridgeAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Serves an EventBridge request through <see cref="EventBridgeWireProtocol"/>.</summary>
    private async Task<HttpResponseMessage> SendEventBridgeAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var body = await PooledRequestBody.ReadAsync(request.Content, cancellationToken).ConfigureAwait(false);
        using var output = PooledArrayBufferWriter.Rent();
        var client = new InternalEventBridgeClient(_cloud, AwsRequestRegion.FromRequest(request));
        var wire = await EventBridgeWireProtocol.HandleAsync(
            EventBridgeWireProtocol.OperationFromTarget(TargetOf(request)), body.Memory, client, output.Writer, cancellationToken).ConfigureAwait(false);
        return ToHttpResponse(wire, output.Writer);
    }

    /// <summary>The request's <c>X-Amz-Target</c>, which the SDKs send as a request or a content header.</summary>
    private static string? TargetOf(HttpRequestMessage request)
    {
        if (request.Headers.TryGetValues("x-amz-target", out var targets)
            || (request.Content?.Headers.TryGetValues("x-amz-target", out targets) ?? false))
        {
            return targets.FirstOrDefault();
        }

        return null;
    }

    /// <summary>
    /// Serves an SNS request through <see cref="SnsWireProtocol"/>, or the certificate a signed SNS
    /// message names in its <c>SigningCertURL</c>.
    /// </summary>
    private async Task<HttpResponseMessage> SendSnsAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Get && SnsSigningCertificate.IsCertificatePath(request.RequestUri?.AbsolutePath))
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SnsSigningCertificate.Current!.Pem, Encoding.ASCII, SnsSigningCertificate.ContentType)
            };
        }

        using var body = await PooledRequestBody.ReadAsync(request.Content, cancellationToken).ConfigureAwait(false);

        // A GET with the parameters in the query string (e.g. an SNS SubscribeURL) is a valid
        // Query-protocol request; its query stands for the body.
        var parameters = body.Memory.Length == 0 && request.RequestUri is { IsAbsoluteUri: true, Query.Length: > 1 } uri
            ? Encoding.UTF8.GetBytes(uri.Query.Substring(1))
            : body.Memory;

        using var output = PooledArrayBufferWriter.Rent();
        var client = new InternalSnsClient(_cloud, AwsRequestRegion.FromRequest(request));
        var wire = await SnsWireProtocol.HandleAsync(parameters, client, output.Writer, cancellationToken).ConfigureAwait(false);
        return ToHttpResponse(wire, output.Writer);
    }

    /// <summary>
    /// The header for a wire protocol's Content-Type: a media type with at most a charset. Built,
    /// rather than added as text that HttpClient would parse when the SDK reads it.
    /// </summary>
    private static System.Net.Http.Headers.MediaTypeHeaderValue ContentTypeHeader(string contentType)
    {
        var semicolon = contentType.IndexOf(';', StringComparison.Ordinal);
        return semicolon < 0
            ? new System.Net.Http.Headers.MediaTypeHeaderValue(contentType)
            : new System.Net.Http.Headers.MediaTypeHeaderValue(contentType.Substring(0, semicolon))
            {
                CharSet = contentType.Substring(contentType.IndexOf('=', semicolon) + 1),
            };
    }

    /// <summary>A wire protocol's answer as an <see cref="HttpResponseMessage"/>, its body copied out of the pooled buffer.</summary>
    private static HttpResponseMessage ToHttpResponse(in WireResponse wire, ArrayBufferWriter<byte> body)
    {
        var response = new HttpResponseMessage(wire.Status) { Content = new ByteArrayContent(body.WrittenSpan.ToArray()) };
        response.Content.Headers.ContentType = ContentTypeHeader(wire.ContentType);
        response.Headers.TryAddWithoutValidation("x-amzn-RequestId", wire.RequestId);
        if (wire.QueryError is not null)
        {
            response.Headers.TryAddWithoutValidation(SqsQueryCompatibleErrors.HeaderName, wire.QueryError);
        }

        if (wire.Crc32 is not null)
        {
            response.Headers.TryAddWithoutValidation("x-amz-crc32", wire.Crc32);
        }

        return response;
    }

    /// <summary>
    /// Serves an SQS request over whichever wire protocol its Content-Type names (see
    /// <see cref="SqsWireProtocol"/>): AWS Query for a form-encoded body, JSON for
    /// <c>application/x-amz-json-1.0</c> with an <c>X-Amz-Target</c>. Anything else is refused as
    /// an unknown operation, and an oversized body with HTTP 413, before any SQS code runs.
    /// </summary>
    private async Task<HttpResponseMessage> SendSqsAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var mediaType = request.Content?.Headers.ContentType?.MediaType;
        using var body = await PooledRequestBody.ReadAsync(request.Content, cancellationToken).ConfigureAwait(false);
        if (SqsWireProtocol.IsTooLarge(body.Memory.Span, SqsWireProtocol.IsForm(mediaType)))
        {
            return Refusal(SqsWireProtocol.RequestEntityTooLargeStatus, SqsWireProtocol.RequestEntityTooLargeBody, "text/html");
        }

        var query = request.RequestUri?.IsAbsoluteUri == true ? request.RequestUri.Query : string.Empty;
        if (SqsWireProtocol.IsForm(mediaType) || (body.Memory.Length == 0 && query.Contains("Action=", StringComparison.Ordinal)))
        {
            // A GET carries its Query parameters in the URL rather than the body.
            var parameters = QueryStringParser.Parse(body.Memory.Length > 0 ? body.Memory.Span : Encoding.UTF8.GetBytes(query.TrimStart('?')));
            var client = new InternalSqsClient(_cloud, AwsRequestRegion.FromRequest(request)) { Caller = SqsCaller.FromRequest(request) };
            return await SendSqsQueryAsync(parameters, client, cancellationToken).ConfigureAwait(false);
        }

        var operationName = SqsWireProtocol.OperationFromTarget(TargetOf(request));
        if (!SqsWireProtocol.IsJson(mediaType))
        {
            return Refusal(SqsWireProtocol.UnknownOperationXmlStatus, SqsWireProtocol.UnknownOperationXml, "application/xml");
        }

        if (operationName is null || !SqsWireProtocol.IsOperation(operationName))
        {
            return Refusal(SqsWireProtocol.UnknownOperationJsonStatus, SqsWireProtocol.UnknownOperationJson, SqsWireProtocol.JsonContentType);
        }

        using var output = PooledArrayBufferWriter.Rent();
        var jsonClient = new InternalSqsClient(_cloud, AwsRequestRegion.FromRequest(request)) { Caller = SqsCaller.FromRequest(request) };
        var wire = await SqsWireProtocol.HandleJsonAsync(operationName, body.Memory, jsonClient, output.Writer, cancellationToken).ConfigureAwait(false);
        return ToHttpResponse(wire, output.Writer);
    }

    /// <summary>Serves a DynamoDB or DynamoDB Streams request (see <see cref="DynamoDbWireProtocol"/>).</summary>
    private async Task<HttpResponseMessage> SendDynamoDbAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var body = await PooledRequestBody.ReadAsync(request.Content, cancellationToken).ConfigureAwait(false);
        using var output = PooledArrayBufferWriter.Rent();
        var wire = DynamoDbWireProtocol.ConflictingSignatures(
                // Authorization, not Headers.Authorization: .NET can't parse a SigV4 value, so that is null.
                request.Headers.Contains("Authorization"),
                request.RequestUri?.Query.Contains("X-Amz-Algorithm=", StringComparison.Ordinal) == true,
                output.Writer)
            ?? await DynamoDbWireProtocol.HandleAsync(
                TargetOf(request), body.Memory, _cloud, AwsRequestRegion.FromRequest(request), output.Writer, cancellationToken).ConfigureAwait(false);
        return ToHttpResponse(wire, output.Writer);
    }

    /// <summary>
    /// Serves an S3 request (see <see cref="S3WireProtocol"/>): the bucket and key from the host or
    /// the path as sent (still percent-encoded), the headers of the request and its content.
    /// </summary>
    private async Task<HttpResponseMessage> SendS3Async(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var body = await PooledRequestBody.ReadAsync(request.Content, cancellationToken).ConfigureAwait(false);
        // Multi-valued headers joined as HttpClient writes them on the wire (and as clients sign them).
        var headers = new List<KeyValuePair<string, string>>();
        foreach (var header in request.Headers)
        {
            headers.Add(new KeyValuePair<string, string>(header.Key, string.Join(", ", header.Value)));
        }

        if (request.Content is not null)
        {
            foreach (var header in request.Content.Headers)
            {
                headers.Add(new KeyValuePair<string, string>(header.Key, string.Join(", ", header.Value)));
            }
        }

        // The Host header a client signs, which HttpClient would add on the wire.
        if (request.Headers.Host is null && request.RequestUri is { IsAbsoluteUri: true } requestUri)
        {
            headers.Add(new KeyValuePair<string, string>("Host", requestUri.Authority));
        }

        var (host, path, query) = SplitUri(request.RequestUri);
        var wireRequest = new S3WireRequest(request.Method.Method, host, path, query, headers, body.Memory)
        {
            Region = AwsRequestRegion.FromRequest(request),
            SecureTransport = !string.Equals(request.RequestUri?.Scheme, "http", StringComparison.OrdinalIgnoreCase),
        };
        var wire = await S3WireProtocol.HandleAsync(wireRequest, _cloud, cancellationToken).ConfigureAwait(false);
        var payload = wire.Body;
        var content = System.Runtime.InteropServices.MemoryMarshal.TryGetArray(payload, out var segment) && segment.Array is not null
            ? new ByteArrayContent(segment.Array, segment.Offset, segment.Count)
            : new ByteArrayContent(payload.ToArray());
        var response = new HttpResponseMessage((HttpStatusCode)wire.Status) { Content = content };
        foreach (var header in wire.Headers)
        {
            if (string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                content.Headers.ContentLength = long.Parse(header.Value, System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (!response.Headers.TryAddWithoutValidation(header.Key, header.Value))
            {
                content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        if (wire.OmitBody && wire.Header("Content-Length") is null)
        {
            content.Headers.ContentLength = 0;
        }

        return response;
    }

    /// <summary>
    /// The host, the path and the query of a request URI as the client wrote them: S3 keys may hold
    /// characters (<c>..</c>, <c>%2F</c>) that <see cref="Uri"/> would normalize.
    /// </summary>
    private static (string Host, string Path, string Query) SplitUri(Uri? uri)
    {
        if (uri is null)
        {
            return (string.Empty, "/", string.Empty);
        }

        var text = uri.OriginalString;
        var scheme = text.IndexOf("://", StringComparison.Ordinal);
        var pathStart = scheme < 0 ? 0 : text.IndexOf('/', scheme + 3);
        if (pathStart < 0)
        {
            pathStart = text.Length;
        }

        var queryStart = text.IndexOf('?', pathStart);
        var path = queryStart < 0 ? text.Substring(pathStart) : text.Substring(pathStart, queryStart - pathStart);
        var query = queryStart < 0 ? string.Empty : text.Substring(queryStart + 1);
        var fragment = query.IndexOf('#', StringComparison.Ordinal);
        if (fragment >= 0)
        {
            query = query.Substring(0, fragment);
        }

        return (uri.IsAbsoluteUri ? uri.Host : string.Empty, path.Length == 0 ? "/" : path, query);
    }

    private static async Task<HttpResponseMessage> SendSqsQueryAsync(Dictionary<string, string> parameters, InternalSqsClient client, CancellationToken cancellationToken)
    {
        using var output = PooledArrayBufferWriter.Rent();
        var wire = await SqsWireProtocol.HandleQueryAsync(parameters, client, output.Writer, cancellationToken).ConfigureAwait(false);
        return ToHttpResponse(wire, output.Writer);
    }

    private static HttpResponseMessage Refusal(HttpStatusCode statusCode, string body, string contentType)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, contentType)
        };
        response.Headers.Add("x-amzn-RequestId", Guid.NewGuid().ToString());
        return response;
    }
}
#endif

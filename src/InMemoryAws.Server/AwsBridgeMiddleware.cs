using System.Buffers;
using System.Text;
using InMemoryAws.Http;
using InMemoryAws.Http.Handlers;
using Microsoft.AspNetCore.Http;

namespace InMemoryAws.Server;

/// <summary>
/// ASP.NET Core middleware that routes each request to its service's wire protocol
/// (<see cref="SqsWireProtocol"/>, <see cref="SnsWireProtocol"/>, <see cref="EventBridgeWireProtocol"/>,
/// <see cref="DynamoDbWireProtocol"/>), the same ones the library's handler calls, and copies the
/// answer onto the response.
/// </summary>
internal sealed class AwsBridgeMiddleware
{
    private const int InitialBufferSize = 4096;

    private readonly CloudRegistry _registry;

    public AwsBridgeMiddleware(CloudRegistry registry)
    {
        _registry = registry;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var cancellationToken = context.RequestAborted;

        // The SigningCertURL of a signed SNS message; the same certificate serves every account.
        if (HttpMethods.IsGet(context.Request.Method) && SnsSigningCertificate.IsCertificatePath(context.Request.Path.Value))
        {
            context.Response.ContentType = SnsSigningCertificate.ContentType;
            await context.Response.WriteAsync(SnsSigningCertificate.Current!.Pem, cancellationToken).ConfigureAwait(false);
            return;
        }

        var cloud = ResolveCloud(context.Request);

        // Read the request body upfront using a pooled buffer - SNS Query protocol
        // needs it for both operation detection (Action param) and handler deserialization
        var initialBuffer = ArrayPool<byte>.Shared.Rent(InitialBufferSize);
        var ms = new PooledMemoryStream(initialBuffer);
        try
        {
            await context.Request.Body.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
            var bodyBuffer = ms.GetBuffer();
            var bodyLength = (int)ms.Length;

            // A GET with the parameters in the query string (e.g. an SNS SubscribeURL) is a valid
            // Query-protocol request; present the query to the handler as if it were the body.
            var parametersInUrl = bodyLength == 0 && context.Request.QueryString.HasValue;
            if (parametersInUrl)
            {
                bodyBuffer = Encoding.UTF8.GetBytes(context.Request.QueryString.Value!.TrimStart('?'));
                bodyLength = bodyBuffer.Length;
            }

            try
            {
                var mediaType = MediaTypeOf(context.Request.ContentType);
                var hasTarget = TryGetJsonTarget(context.Request, out var targetService, out var jsonOperation);
                if (IsS3Request(context.Request, hasTarget, mediaType))
                {
                    await HandleS3Async(context, ms.GetBuffer().AsMemory(0, (int)ms.Length), cloud, cancellationToken).ConfigureAwait(false);
                    return;
                }

                // SQS, EventBridge and DynamoDB use the JSON protocol with an x-amz-target header, told apart
                // by the target's prefix (AmazonSQS.*, AWSEvents.*, DynamoDB_20120810.*). SNS, and SQS too, speak the
                // AWS Query protocol: form-encoded parameters, told apart by their API Version.
                if (hasTarget && DynamoDbWireProtocol.IsTarget(context.Request.Headers["x-amz-target"].ToString()))
                {
                    await HandleDynamoDbAsync(context, bodyBuffer.AsMemory(0, bodyLength), cloud, cancellationToken).ConfigureAwait(false);
                }
                else if (hasTarget && string.Equals(targetService, EventBridgeWireProtocol.TargetPrefix, StringComparison.Ordinal))
                {
                    await HandleEventBridgeAsync(context, jsonOperation, bodyBuffer.AsMemory(0, bodyLength), cloud, cancellationToken).ConfigureAwait(false);
                }
                else if (SqsWireProtocol.IsForm(mediaType) || parametersInUrl)
                {
                    var parameters = QueryStringParser.Parse(bodyBuffer.AsSpan(0, bodyLength));
                    if (SqsWireProtocol.IsSqsQueryRequest(parameters, context.Request.Headers.Authorization.ToString()))
                    {
                        await HandleSqsQueryAsync(context, bodyBuffer.AsMemory(0, bodyLength), parameters, cloud, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await HandleSnsAsync(context, bodyBuffer.AsMemory(0, bodyLength), cloud, cancellationToken).ConfigureAwait(false);
                    }
                }
                else if (hasTarget || SqsWireProtocol.IsJson(mediaType))
                {
                    await HandleSqsJsonAsync(context, bodyBuffer, bodyLength, mediaType, hasTarget ? jsonOperation : null, cloud, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await HandleSnsAsync(context, bodyBuffer.AsMemory(0, bodyLength), cloud, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
#pragma warning disable CA1031
            catch (Exception ex)
#pragma warning restore CA1031
            {
                await WriteErrorResponseAsync(context, ex, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await ms.DisposeAsync().ConfigureAwait(false);
            ArrayPool<byte>.Shared.Return(initialBuffer, clearArray: true);
        }
    }

    /// <summary>
    /// An SQS request over the JSON protocol. As in <c>InMemoryAwsHttpMessageHandler</c>, a target
    /// sent with any Content-Type but <c>application/x-amz-json-1.0</c>, or that Content-Type without
    /// a known target, is refused as an unknown operation; an oversized body with HTTP 413.
    /// </summary>
    private static async Task HandleSqsJsonAsync(
        HttpContext context, byte[] body, int bodyLength, string? mediaType, string? operation, InMemoryAwsCloud cloud, CancellationToken cancellationToken)
    {
        if (SqsWireProtocol.IsTooLarge(body.AsSpan(0, bodyLength), isForm: false))
        {
            await WriteRefusalAsync(context, SqsWireProtocol.RequestEntityTooLargeStatus, SqsWireProtocol.RequestEntityTooLargeBody, "text/html", cancellationToken).ConfigureAwait(false);
        }
        else if (!SqsWireProtocol.IsJson(mediaType))
        {
            await WriteRefusalAsync(context, SqsWireProtocol.UnknownOperationXmlStatus, SqsWireProtocol.UnknownOperationXml, "application/xml", cancellationToken).ConfigureAwait(false);
        }
        else if (operation is null || !SqsWireProtocol.IsOperation(operation))
        {
            await WriteRefusalAsync(context, SqsWireProtocol.UnknownOperationJsonStatus, SqsWireProtocol.UnknownOperationJson, SqsWireProtocol.JsonContentType, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            using var output = PooledArrayBufferWriter.Rent();
            var client = new InternalSqsClient(cloud, AwsRequestRegion.FromHttpContext(context)) { Caller = SqsCaller.FromHttpContext(context) };
            var wire = await SqsWireProtocol.HandleJsonAsync(operation, body.AsMemory(0, bodyLength), client, output.Writer, cancellationToken).ConfigureAwait(false);
            await WriteAsync(context, wire, output.Writer, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>An EventBridge request (see <see cref="EventBridgeWireProtocol"/>).</summary>
    private static async Task HandleEventBridgeAsync(
        HttpContext context, string operation, ReadOnlyMemory<byte> body, InMemoryAwsCloud cloud, CancellationToken cancellationToken)
    {
        using var output = PooledArrayBufferWriter.Rent();
        var client = new InternalEventBridgeClient(cloud, AwsRequestRegion.FromHttpContext(context));
        var wire = await EventBridgeWireProtocol.HandleAsync(operation, body, client, output.Writer, cancellationToken).ConfigureAwait(false);
        await WriteAsync(context, wire, output.Writer, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>An SNS request (see <see cref="SnsWireProtocol"/>).</summary>
    private static async Task HandleSnsAsync(HttpContext context, ReadOnlyMemory<byte> parameters, InMemoryAwsCloud cloud, CancellationToken cancellationToken)
    {
        using var output = PooledArrayBufferWriter.Rent();
        var client = new InternalSnsClient(cloud, AwsRequestRegion.FromHttpContext(context));
        var wire = await SnsWireProtocol.HandleAsync(parameters, client, output.Writer, cancellationToken).ConfigureAwait(false);
        await WriteAsync(context, wire, output.Writer, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The <c>x-amzn-RequestId</c> of a response no wire protocol answered.</summary>
    private static void SetRequestId(HttpContext context) => context.Response.Headers["x-amzn-RequestId"] = WireResponse.NewRequestId();

    /// <summary>Copies a wire protocol's answer onto the response.</summary>
    private static async Task WriteAsync(HttpContext context, WireResponse wire, ArrayBufferWriter<byte> body, CancellationToken cancellationToken)
    {
        var response = context.Response;
        response.StatusCode = (int)wire.Status;
        response.ContentType = wire.ContentType;
        response.Headers["x-amzn-RequestId"] = wire.RequestId;
        if (wire.QueryError is not null)
        {
            response.Headers[SqsQueryCompatibleErrors.HeaderName] = wire.QueryError;
        }

        if (wire.Crc32 is not null)
        {
            response.Headers["x-amz-crc32"] = wire.Crc32;
        }

        await response.Body.WriteAsync(body.WrittenMemory, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A DynamoDB or DynamoDB Streams request (see <see cref="DynamoDbWireProtocol"/>).</summary>
    private static async Task HandleDynamoDbAsync(HttpContext context, ReadOnlyMemory<byte> body, InMemoryAwsCloud cloud, CancellationToken cancellationToken)
    {
        using var output = PooledArrayBufferWriter.Rent();
        var wire = DynamoDbWireProtocol.ConflictingSignatures(context.Request.Headers.ContainsKey("Authorization"), context.Request.Query.ContainsKey("X-Amz-Algorithm"), output.Writer)
            ?? await DynamoDbWireProtocol.HandleAsync(
                context.Request.Headers["x-amz-target"].ToString(),
                body,
                cloud,
                AwsRequestRegion.FromHttpContext(context),
                output.Writer,
                cancellationToken).ConfigureAwait(false);
        await WriteAsync(context, wire, output.Writer, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether a request is for S3, which has no target header and no Action parameter: signed for
    /// <c>s3</c> (a SigV4 scope, a presigned URL's credential, or a SigV2 <c>AWS</c> header), sent to a
    /// host with an <c>s3</c> label, or anything unsigned that no other protocol claims.
    /// </summary>
    internal static bool IsS3Request(HttpRequest request, bool hasTarget, string? mediaType)
    {
        var authorization = request.Headers.Authorization.ToString();
        if (authorization.StartsWith("AWS ", StringComparison.Ordinal))
        {
            return true;
        }

        if (ScopeService(authorization) is { } service)
        {
            return service is "s3" or "s3express";
        }

        if (request.Query.TryGetValue("X-Amz-Credential", out var credential))
        {
            var parts = credential.ToString().Split('/');
            return parts.Length >= 5 && parts[3] is "s3" or "s3express";
        }

        var host = request.Host.Host;
        if (host.StartsWith("s3.", StringComparison.OrdinalIgnoreCase) || host.Contains(".s3.", StringComparison.OrdinalIgnoreCase)
            || host.StartsWith("s3-", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !hasTarget
            && !SqsWireProtocol.IsForm(mediaType)
            && !SqsWireProtocol.IsJson(mediaType)
            && !request.Query.ContainsKey("Action");
    }

    /// <summary>The service of a SigV4 <c>Authorization</c> header's credential scope; null when it has none.</summary>
    private static string? ScopeService(string authorization)
    {
        var start = authorization.IndexOf("Credential=", StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        var end = authorization.IndexOfAny([',', ' '], start);
        var parts = (end < 0 ? authorization[(start + "Credential=".Length)..] : authorization[(start + "Credential=".Length)..end]).Split('/');
        return parts.Length >= 5 ? parts[3] : null;
    }

    /// <summary>An S3 request (see <see cref="S3WireProtocol"/>), its path and query as sent.</summary>
    private static async Task HandleS3Async(HttpContext context, ReadOnlyMemory<byte> body, InMemoryAwsCloud cloud, CancellationToken cancellationToken)
    {
        var request = context.Request;
        var rawTarget = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestFeature>()?.RawTarget;
        string path;
        string query;
        if (!string.IsNullOrEmpty(rawTarget) && rawTarget[0] == '/')
        {
            var question = rawTarget.IndexOf('?', StringComparison.Ordinal);
            path = question < 0 ? rawTarget : rawTarget[..question];
            query = question < 0 ? string.Empty : rawTarget[(question + 1)..];
        }
        else
        {
            path = (request.PathBase + request.Path).ToUriComponent();
            query = request.QueryString.HasValue ? request.QueryString.Value![1..] : string.Empty;
        }

        var headers = new List<KeyValuePair<string, string>>();
        foreach (var header in request.Headers)
        {
            headers.Add(new KeyValuePair<string, string>(header.Key, header.Value.ToString()));
        }

        var wireRequest = new S3WireRequest(request.Method, request.Host.Host, path, query, headers, body)
        {
            Region = AwsRequestRegion.FromHttpContext(context),
            SecureTransport = request.IsHttps,
        };
        var wire = await S3WireProtocol.HandleAsync(wireRequest, cloud, cancellationToken).ConfigureAwait(false);
        var response = context.Response;
        response.StatusCode = wire.Status;
        foreach (var header in wire.Headers)
        {
            if (string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                response.ContentLength = long.Parse(header.Value, System.Globalization.CultureInfo.InvariantCulture);
            }
            else
            {
                response.Headers[header.Key] = header.Value;
            }
        }

        var payload = wire.Body;
        if (wire.OmitBody)
        {
            return;
        }

        response.ContentLength ??= payload.Length;
        await response.Body.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>An SQS request over the AWS Query protocol: form-encoded parameters, an XML response.</summary>
    private static async Task HandleSqsQueryAsync(
        HttpContext context, ReadOnlyMemory<byte> body, Dictionary<string, string> parameters, InMemoryAwsCloud cloud, CancellationToken cancellationToken)
    {
        if (SqsWireProtocol.IsTooLarge(body.Span, isForm: true))
        {
            await WriteRefusalAsync(context, SqsWireProtocol.RequestEntityTooLargeStatus, SqsWireProtocol.RequestEntityTooLargeBody, "text/html", cancellationToken).ConfigureAwait(false);
            return;
        }

        using var output = PooledArrayBufferWriter.Rent();
        var client = new InternalSqsClient(cloud, AwsRequestRegion.FromHttpContext(context)) { Caller = SqsCaller.FromHttpContext(context) };
        var wire = await SqsWireProtocol.HandleQueryAsync(parameters, client, output.Writer, cancellationToken).ConfigureAwait(false);
        await WriteAsync(context, wire, output.Writer, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteRefusalAsync(HttpContext context, System.Net.HttpStatusCode status, string body, string contentType, CancellationToken cancellationToken)
    {
        SetRequestId(context);
        context.Response.StatusCode = (int)status;
        context.Response.ContentType = contentType;
        await context.Response.Body.WriteAsync(SqsWireProtocol.Utf8(body), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The media type of a Content-Type header, without its parameters.</summary>
    private static string? MediaTypeOf(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType))
        {
            return null;
        }

        var semicolon = contentType.IndexOf(';', StringComparison.Ordinal);
        return (semicolon >= 0 ? contentType[..semicolon] : contentType).Trim();
    }

    private InMemoryAwsCloud ResolveCloud(HttpRequest request)
    {
        var accessKey = ExtractAccessKey(request);
        if (AwsAccountId.IsValid(accessKey))
        {
            return _registry.GetOrCreate(accessKey);
        }

        return _registry.DefaultCloud;
    }

    internal static string? ExtractAccessKey(HttpRequest request)
    {
        // AWS Signature V4 format:
        // Authorization: AWS4-HMAC-SHA256 Credential=ACCESS_KEY_ID/20230101/us-east-1/sqs/aws4_request, ...
        var authHeader = request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(authHeader))
        {
            return null;
        }

        var credentialIndex = authHeader.IndexOf("Credential=", StringComparison.Ordinal);
        if (credentialIndex < 0)
        {
            return null;
        }

        var start = credentialIndex + "Credential=".Length;
        var slashIndex = authHeader.IndexOf('/', start);
        if (slashIndex < 0)
        {
            return null;
        }

        return authHeader[start..slashIndex];
    }

    private static bool TryGetJsonTarget(HttpRequest request, out string service, out string operation)
    {
        service = string.Empty;
        operation = string.Empty;

        // JSON-protocol services (SQS, EventBridge) send "Service.Operation" in x-amz-target.
        if (!request.Headers.TryGetValue("x-amz-target", out var values))
        {
            return false;
        }

        var target = values.ToString();
        var parts = target.Split('.');
        if (parts.Length != 2)
        {
            return false;
        }

        service = parts[0];
        operation = parts[1];
        return true;
    }

    /// <summary>
    /// The answer to a failure outside every wire protocol (each answers its own): an
    /// <c>InternalFailure</c> in AWS's JSON error format.
    /// </summary>
    private static async Task WriteErrorResponseAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        using var output = PooledArrayBufferWriter.Rent();
        var wire = AwsJsonErrors.Write(output.Writer, exception, "com.amazon.coral.service", SqsWireProtocol.JsonContentType, queryCompatible: false);
        await WriteAsync(context, wire, output.Writer, cancellationToken).ConfigureAwait(false);
    }
}

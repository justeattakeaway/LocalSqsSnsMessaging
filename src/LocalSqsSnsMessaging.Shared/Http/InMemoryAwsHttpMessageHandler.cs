#if !ASPNETCORE
using System.Buffers;
using System.Net;
using System.Text;
#if NETSTANDARD2_0
using LocalSqsSnsMessaging.Http.Internal;
#endif

namespace LocalSqsSnsMessaging.Http;

/// <summary>
/// HTTP message handler that intercepts AWS SDK HTTP requests and routes them to in-memory implementations.
/// </summary>
/// <remarks>
/// Use this handler when you need to plug the in-memory bus into an existing HTTP pipeline
/// (for example, a custom <see cref="Amazon.Runtime.HttpClientFactory"/>). For most testing
/// scenarios, prefer the <c>CreateSqsClient</c>/<c>CreateSnsClient</c> extensions on
/// <see cref="InMemoryAwsBus"/>, or use <see cref="InMemoryAwsHttpClientFactory"/>.
/// </remarks>
public sealed class InMemoryAwsHttpMessageHandler : DelegatingHandler
{
    private readonly InMemoryAwsBus _bus;
    private readonly AwsServiceType _serviceType;
    private readonly AwsProtocolType _protocolType;

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryAwsHttpMessageHandler"/> class.
    /// </summary>
    /// <param name="bus">The in-memory AWS bus instance that will handle the intercepted requests.</param>
    /// <param name="serviceType">The AWS service whose protocol this handler should speak.</param>
    public InMemoryAwsHttpMessageHandler(InMemoryAwsBus bus, AwsServiceType serviceType)
    {
        ArgumentNullException.ThrowIfNull(bus);
        _bus = bus;
        _serviceType = serviceType;

        // Determine protocol based on service type
        _protocolType = serviceType switch
        {
            AwsServiceType.Sqs => AwsProtocolType.Json,
            AwsServiceType.EventBridge => AwsProtocolType.Json,
            AwsServiceType.Sns => AwsProtocolType.Query,
            AwsServiceType.DynamoDb or AwsServiceType.DynamoDbStreams => AwsProtocolType.Json,
            _ => throw new NotSupportedException($"Service type {serviceType} is not supported.")
        };
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

        if (_serviceType == AwsServiceType.Sns)
        {
            return await SendSnsAsync(request, cancellationToken).ConfigureAwait(false);
        }

        // Extract operation name from request
        string operationName;
        try
        {
            // For JSON protocol (EventBridge), the operation is in the x-amz-target header
            operationName = ExtractOperationNameFromHeader(request);
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return CreateErrorResponse(ex);
        }

#if NET
        using var activity = InMemoryAwsBus.ActivitySource.StartActivity(
            $"EventBridge.{operationName}",
            System.Diagnostics.ActivityKind.Client);
#endif

        try
        {
            var response = await Handlers.EventBridgeOperationHandler.HandleAsync(
                request, operationName, _bus, cancellationToken).ConfigureAwait(false);

            // Add AWS response headers
            response.Headers.Add("x-amzn-RequestId", Guid.NewGuid().ToString());

            return response;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
#if NET
            if (activity is not null)
            {
                activity.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.Message);
                activity.SetTag("error.type", ex.GetType().FullName);
            }
#endif
            // Convert exceptions to AWS error responses
            return CreateErrorResponse(ex);
        }
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
        var client = new InternalSnsClient(_bus, AwsRequestRegion.FromRequest(request));
        var wire = await SnsWireProtocol.HandleAsync(parameters, client, output.Writer, cancellationToken).ConfigureAwait(false);
        return ToHttpResponse(wire, output.Writer);
    }

    /// <summary>A wire protocol's answer as an <see cref="HttpResponseMessage"/>, its body copied out of the pooled buffer.</summary>
    private static HttpResponseMessage ToHttpResponse(in WireResponse wire, ArrayBufferWriter<byte> body)
    {
        var response = new HttpResponseMessage(wire.Status) { Content = new ByteArrayContent(body.WrittenSpan.ToArray()) };
        response.Content.Headers.TryAddWithoutValidation("Content-Type", wire.ContentType);
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
        var content = request.Content;
        var mediaType = content?.Headers.ContentType?.MediaType;

        byte[]? body = null;
        var length = content?.Headers.ContentLength;
        if (content is not null && (length is null || length > SqsWireProtocol.MaxRequestBodyBytes))
        {
            // Buffered by HttpContent, so the JSON handler can still read it afterwards.
            body = await content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            length = body.Length;
        }

        if (body is not null && SqsWireProtocol.IsTooLarge(body, SqsWireProtocol.IsForm(mediaType)))
        {
            return Refusal(SqsWireProtocol.RequestEntityTooLargeStatus, SqsWireProtocol.RequestEntityTooLargeBody, "text/html");
        }

        var query = request.RequestUri?.IsAbsoluteUri == true ? request.RequestUri.Query : string.Empty;
        if (SqsWireProtocol.IsForm(mediaType) || ((length ?? 0) == 0 && query.Contains("Action=", StringComparison.Ordinal)))
        {
            body ??= content is not null ? await content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false) : [];
            // A GET carries its Query parameters in the URL rather than the body.
            var parameters = QueryStringParser.Parse(body.Length > 0 ? body : Encoding.UTF8.GetBytes(query.TrimStart('?')));
            var client = new InternalSqsClient(_bus, AwsRequestRegion.FromRequest(request)) { Caller = SqsCaller.FromRequest(request) };
            return await SendSqsQueryAsync(parameters, client, cancellationToken).ConfigureAwait(false);
        }

        if (!request.Headers.TryGetValues("x-amz-target", out var targets))
        {
            content?.Headers.TryGetValues("x-amz-target", out targets);
        }

        var operationName = SqsWireProtocol.OperationFromTarget(targets?.FirstOrDefault());
        if (!SqsWireProtocol.IsJson(mediaType))
        {
            return Refusal(SqsWireProtocol.UnknownOperationXmlStatus, SqsWireProtocol.UnknownOperationXml, "application/xml");
        }

        if (operationName is null || !Handlers.SqsQueryOperationHandler.IsOperation(operationName))
        {
            return Refusal(SqsWireProtocol.UnknownOperationJsonStatus, SqsWireProtocol.UnknownOperationJson, SqsWireProtocol.JsonContentType);
        }

#if NET
        using var activity = InMemoryAwsBus.ActivitySource.StartActivity($"SQS.{operationName}", System.Diagnostics.ActivityKind.Client);
#endif
        try
        {
            var response = await Handlers.SqsOperationHandler.HandleAsync(request, operationName, _bus, cancellationToken).ConfigureAwait(false);
            response.Headers.Add("x-amzn-RequestId", Guid.NewGuid().ToString());
            return response;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
#if NET
            if (activity is not null)
            {
                activity.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.Message);
                activity.SetTag("error.type", ex.GetType().FullName);
            }
#endif
            return CreateErrorResponse(ex);
        }
    }

    /// <summary>
    /// Serves a DynamoDB or DynamoDB Streams request (see <see cref="DynamoDbWireProtocol"/>), with
    /// the <c>x-amz-crc32</c> header DynamoDB sends.
    /// </summary>
    private async Task<HttpResponseMessage> SendDynamoDbAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is not null ? await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false) : [];
        if (!request.Headers.TryGetValues("x-amz-target", out var targets))
        {
            request.Content?.Headers.TryGetValues("x-amz-target", out targets);
        }

        var result = DynamoDbWireProtocol.ConflictingSignatures(
                // Authorization, not Headers.Authorization: .NET can't parse a SigV4 value, so that is null.
                request.Headers.Contains("Authorization"),
                request.RequestUri?.Query.Contains("X-Amz-Algorithm=", StringComparison.Ordinal) == true)
            ?? await DynamoDbWireProtocol.HandleAsync(
                targets?.FirstOrDefault(), body, _bus, AwsRequestRegion.FromRequest(request), cancellationToken).ConfigureAwait(false);
        var response = new HttpResponseMessage(result.Status) { Content = new ByteArrayContent(result.Body) };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(DynamoDbWireProtocol.ContentType);
        response.Headers.Add("x-amzn-RequestId", Guid.NewGuid().ToString());
        response.Headers.TryAddWithoutValidation("x-amz-crc32", result.Crc32);
        return response;
    }

    private static async Task<HttpResponseMessage> SendSqsQueryAsync(Dictionary<string, string> parameters, InternalSqsClient client, CancellationToken cancellationToken)
    {
#if NET
        using var activity = InMemoryAwsBus.ActivitySource.StartActivity(
            $"SQS.{parameters.GetValueOrDefault("Action")}", System.Diagnostics.ActivityKind.Client);
#endif
        using var buffer = PooledArrayBufferWriter.Rent();
        var status = await Handlers.SqsQueryOperationHandler.HandleAsync(parameters, client, buffer.Writer, cancellationToken).ConfigureAwait(false);
#if NET
        if (activity is not null && status != HttpStatusCode.OK)
        {
            activity.SetStatus(System.Diagnostics.ActivityStatusCode.Error);
        }
#endif
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(buffer.Writer.WrittenSpan.ToArray()) };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(Handlers.SqsQueryOperationHandler.ContentType);
        response.Headers.Add("x-amzn-RequestId", Guid.NewGuid().ToString());
        return response;
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

    private static string ExtractOperationNameFromHeader(HttpRequestMessage request)
    {
        // Try x-amz-target header (used by JSON protocol like SQS)
        if (request.Headers.TryGetValues("x-amz-target", out var targetValues) ||
            (request.Content?.Headers.TryGetValues("x-amz-target", out targetValues) ?? false))
        {
            var target = targetValues.FirstOrDefault();
            if (!string.IsNullOrEmpty(target))
            {
                var parts = target.Split('.');
                if (parts.Length == 2)
                {
                    return parts[1];
                }
            }
        }

        throw new InvalidOperationException($"Unable to determine operation name from x-amz-target header. URI: {request.RequestUri}");
    }

    private static HttpResponseMessage CreateErrorResponse(Exception exception)
    {
        var errorType = exception.GetType().Name;
        var errorMessage = exception.Message;

        var errorJson = $$"""
        {
            "__type": "{{errorType}}",
            "message": "{{errorMessage.Replace("\"", "\\\"", StringComparison.Ordinal)}}"
        }
        """;

        var statusCode = exception switch
        {
            InternalNotFoundException => HttpStatusCode.NotFound,
            AwsServiceException awsEx => awsEx.StatusCode,
            _ => HttpStatusCode.InternalServerError
        };

        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(errorJson, Encoding.UTF8, "application/x-amz-json-1.0")
        };
    }
}
#endif

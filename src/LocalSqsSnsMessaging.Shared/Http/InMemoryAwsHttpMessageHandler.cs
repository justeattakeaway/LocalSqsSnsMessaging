#if !ASPNETCORE
using System.Net;
using System.Text;

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
            _ => throw new NotSupportedException($"Service type {serviceType} is not supported.")
        };
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Extract operation name from request
        string operationName;
        try
        {
            if (_protocolType == AwsProtocolType.Json)
            {
                // For JSON protocol (SQS), the operation is in the x-amz-target header
                operationName = ExtractOperationNameFromHeader(request);
            }
            else
            {
                // For Query protocol (SNS), the operation is in the Action parameter (in body or query string)
                var requestBody = request.Content != null
                    ? await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
                    : string.Empty;
                operationName = ExtractOperationNameFromBody(request, requestBody);
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
            return CreateErrorResponse(ex);
        }

        var servicePrefix = _serviceType switch
        {
            AwsServiceType.Sqs => "SQS",
            AwsServiceType.EventBridge => "EventBridge",
            _ => "SNS"
        };

#if NET
        using var activity = InMemoryAwsBus.ActivitySource.StartActivity(
            $"{servicePrefix}.{operationName}",
            System.Diagnostics.ActivityKind.Client);
#endif

        try
        {
            // Route to the appropriate handler based on service type.
            HttpResponseMessage response = _serviceType switch
            {
                AwsServiceType.Sqs => await Handlers.SqsOperationHandler.HandleAsync(
                    request, operationName, _bus, cancellationToken).ConfigureAwait(false),
                AwsServiceType.EventBridge => await Handlers.EventBridgeOperationHandler.HandleAsync(
                    request, operationName, _bus, cancellationToken).ConfigureAwait(false),
                _ => await Handlers.SnsOperationHandler.HandleAsync(
                    request, operationName, _bus, cancellationToken).ConfigureAwait(false)
            };

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

    private static string ExtractOperationNameFromBody(HttpRequestMessage request, string requestBody)
    {
        // Try extracting from URL query string (Action parameter - used by Query protocol GET requests)
        if (request.RequestUri?.Query != null)
        {
            var queryParams = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
            var action = queryParams["Action"];
            if (!string.IsNullOrEmpty(action))
            {
                return action;
            }
        }

        // Try extracting from request body (Query protocol POST requests with form-encoded data)
        if (!string.IsNullOrWhiteSpace(requestBody))
        {
            var bodyParams = System.Web.HttpUtility.ParseQueryString(requestBody);
            var action = bodyParams["Action"];
            if (!string.IsNullOrEmpty(action))
            {
                return action;
            }
        }

        throw new InvalidOperationException($"Unable to determine operation name from request body or query string. URI: {request.RequestUri}");
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

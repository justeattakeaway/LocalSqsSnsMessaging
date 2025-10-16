using System.Net;
using System.Text;

namespace LocalSqsSnsMessaging.Http;

/// <summary>
/// HTTP message handler that intercepts AWS SDK HTTP requests and routes them to in-memory implementations.
/// </summary>
internal sealed class InMemoryAwsHttpMessageHandler : DelegatingHandler
{
    private readonly InMemoryAwsBus _bus;
    private readonly AwsServiceType _serviceType;
    private readonly AwsProtocolType _protocolType;

    public InMemoryAwsHttpMessageHandler(InMemoryAwsBus bus, AwsServiceType serviceType)
    {
        ArgumentNullException.ThrowIfNull(bus);
        _bus = bus;
        _serviceType = serviceType;

        // Determine protocol based on service type
        _protocolType = serviceType switch
        {
            AwsServiceType.Sqs => AwsProtocolType.Json,
            AwsServiceType.Sns => AwsProtocolType.Query,
            _ => throw new NotSupportedException($"Service type {serviceType} is not supported.")
        };
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            // Extract request context
            var context = await ExtractRequestContextAsync(request, cancellationToken).ConfigureAwait(false);

            // Serialize response based on protocol type
            HttpResponseMessage response;
            if (_protocolType == AwsProtocolType.Query)
            {
                // Query protocol: XML response using generated serializers
                // SNS returns (object Response, string OperationName) tuple
                var (responseObject, operationName) = await Handlers.SnsOperationHandler.HandleAsync(
                    context, _bus, cancellationToken).ConfigureAwait(false);
                var responseXml = Handlers.SnsQuerySerializers.SerializeResponse(responseObject, operationName);
                response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseXml, Encoding.UTF8, "text/xml")
                };
            }
            else
            {
                // JSON protocol: JSON response using generated serializers
                // SQS returns (object Response, string OperationName) tuple
                var (responseObject, operationName) = await Handlers.SqsOperationHandler.HandleAsync(
                    context, _bus, cancellationToken).ConfigureAwait(false);
                var responseJson = Handlers.SqsJsonSerializers.SerializeResponse(responseObject, operationName);
                response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseJson, Encoding.UTF8, "application/x-amz-json-1.0")
                };
            }

            // Add AWS response headers
            response.Headers.Add("x-amzn-RequestId", Guid.NewGuid().ToString());

            return response;
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // Convert exceptions to AWS error responses
            return CreateErrorResponse(ex);
        }
    }

    private async Task<AwsRequestContext> ExtractRequestContextAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // Read request body
        var requestBody = request.Content != null
            ? await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
            : string.Empty;

        // Extract operation name from x-amz-target header, URL, or body
        var operationName = ExtractOperationName(request, requestBody);

        // Extract headers
        var headers = request.Headers
            .ToDictionary(h => h.Key, h => string.Join(",", h.Value));

        return new AwsRequestContext
        {
            ServiceType = _serviceType,
            OperationName = operationName,
            RequestBody = requestBody,
            Headers = headers
        };
    }

    private static string ExtractOperationName(HttpRequestMessage request, string requestBody)
    {
        // Try x-amz-target header first (used by JSON protocol like DynamoDB)
        // Check both request headers and content headers
        if (request.Headers.TryGetValues("x-amz-target", out var targetValues) ||
            (request.Content?.Headers.TryGetValues("x-amz-target", out targetValues) ?? false))
        {
            var target = targetValues.FirstOrDefault();
            if (!string.IsNullOrEmpty(target))
            {
                // Format is typically "DynamoDB_20120810.GetItem"
                var parts = target.Split('.');
                if (parts.Length == 2)
                {
                    return parts[1];
                }
            }
        }

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
        // SNS and SQS use Query protocol which sends Action in the body for POST requests
        if (!string.IsNullOrWhiteSpace(requestBody))
        {
            // For Query protocol with POST, body is form-urlencoded: "Action=CreateTopic&Name=test&Version=2010-03-31"
            var bodyParams = System.Web.HttpUtility.ParseQueryString(requestBody);
            var action = bodyParams["Action"];
            if (!string.IsNullOrEmpty(action))
            {
                return action;
            }
        }

        throw new InvalidOperationException($"Unable to determine operation name from request. URI: {request.RequestUri}, Headers: {string.Join(", ", request.Headers.Select(h => $"{h.Key}={string.Join(";", h.Value)}"))}");
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
            Amazon.SQS.Model.QueueDoesNotExistException => HttpStatusCode.BadRequest,
            Amazon.SimpleNotificationService.Model.NotFoundException => HttpStatusCode.NotFound,
            Amazon.Runtime.AmazonServiceException => HttpStatusCode.BadRequest,
            _ => HttpStatusCode.InternalServerError
        };

        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(errorJson, Encoding.UTF8, "application/x-amz-json-1.0")
        };
    }
}

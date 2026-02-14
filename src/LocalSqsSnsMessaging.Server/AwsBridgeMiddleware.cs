using System.Buffers;
using System.Net;
using LocalSqsSnsMessaging.Http.Handlers;
using Microsoft.AspNetCore.Http;

namespace LocalSqsSnsMessaging.Server;

/// <summary>
/// ASP.NET Core middleware that bridges between Kestrel's HttpContext and the
/// existing AWS protocol handlers (SqsOperationHandler/SnsOperationHandler).
/// </summary>
internal sealed class AwsBridgeMiddleware
{
    private const int InitialBufferSize = 4096;

    private readonly InMemoryAwsBus _bus;

    public AwsBridgeMiddleware(InMemoryAwsBus bus)
    {
        _bus = bus;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var cancellationToken = context.RequestAborted;

        // Read the request body upfront using a pooled buffer - SNS Query protocol
        // needs it for both operation detection (Action param) and handler deserialization
        var initialBuffer = ArrayPool<byte>.Shared.Rent(InitialBufferSize);
        var ms = new PooledMemoryStream(initialBuffer);
        try
        {
            await context.Request.Body.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
            var bodyBuffer = ms.GetBuffer();
            var bodyLength = (int)ms.Length;

            using var requestMessage = BuildHttpRequestMessage(context.Request, bodyBuffer, bodyLength);

            try
            {
                using var responseMessage = await HandleRequestAsync(context.Request, requestMessage, bodyBuffer, bodyLength, cancellationToken).ConfigureAwait(false);
                responseMessage.Headers.Add("x-amzn-RequestId", Guid.NewGuid().ToString());
                await WriteResponseAsync(context.Response, responseMessage).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
#pragma warning disable CA1031
            catch (Exception ex)
#pragma warning restore CA1031
            {
                using var errorResponse = CreateErrorResponse(ex);
                await WriteResponseAsync(context.Response, errorResponse).ConfigureAwait(false);
            }
        }
        finally
        {
            await ms.DisposeAsync().ConfigureAwait(false);
            ArrayPool<byte>.Shared.Return(initialBuffer, clearArray: true);
        }
    }

    private async Task<HttpResponseMessage> HandleRequestAsync(
        HttpRequest request,
        HttpRequestMessage requestMessage,
        byte[] bodyBytes,
        int bodyLength,
        CancellationToken cancellationToken)
    {
        if (IsSqsRequest(request))
        {
            var operationName = ExtractSqsOperationName(request);
            return await SqsOperationHandler.HandleAsync(
                requestMessage, operationName, _bus, cancellationToken).ConfigureAwait(false);
        }

        var snsOperationName = ExtractSnsOperationName(request, bodyBytes, bodyLength);
        return await SnsOperationHandler.HandleAsync(
            requestMessage, snsOperationName, _bus, cancellationToken).ConfigureAwait(false);
    }

    private static HttpRequestMessage BuildHttpRequestMessage(HttpRequest request, byte[] bodyBytes, int bodyLength)
    {
        var uri = $"{request.Scheme}://{request.Host}{request.Path}{request.QueryString}";
        var requestMessage = new HttpRequestMessage(new HttpMethod(request.Method), uri);

        // Copy request headers
        foreach (var header in request.Headers)
        {
            requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }

        // Set body content using the pooled buffer directly (no copy via ToArray)
        var content = new ByteArrayContent(bodyBytes, 0, bodyLength);
        foreach (var header in request.Headers)
        {
            content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }
        requestMessage.Content = content;

        return requestMessage;
    }

    private static bool IsSqsRequest(HttpRequest request)
    {
        // SQS uses JSON protocol with x-amz-target header
        return request.Headers.ContainsKey("x-amz-target");
    }

    private static string ExtractSqsOperationName(HttpRequest request)
    {
        var target = request.Headers["x-amz-target"].ToString();
        if (!string.IsNullOrEmpty(target))
        {
            var parts = target.Split('.');
            if (parts.Length == 2)
            {
                return parts[1];
            }
        }

        throw new InvalidOperationException("Unable to determine SQS operation name from x-amz-target header.");
    }

    private static string ExtractSnsOperationName(HttpRequest request, byte[] bodyBytes, int bodyLength)
    {
        // Try query string first
        if (request.Query.TryGetValue("Action", out var actionFromQuery) && !string.IsNullOrEmpty(actionFromQuery))
        {
            return actionFromQuery.ToString();
        }

        // Try form-encoded body
        if (bodyLength > 0)
        {
            var bodyString = Encoding.UTF8.GetString(bodyBytes, 0, bodyLength);
            var bodyParams = System.Web.HttpUtility.ParseQueryString(bodyString);
            var action = bodyParams["Action"];
            if (!string.IsNullOrEmpty(action))
            {
                return action;
            }
        }

        throw new InvalidOperationException("Unable to determine SNS operation name from request.");
    }

    private static async Task WriteResponseAsync(HttpResponse response, HttpResponseMessage responseMessage)
    {
        response.StatusCode = (int)responseMessage.StatusCode;

        // Copy response headers
        foreach (var header in responseMessage.Headers)
        {
            response.Headers[header.Key] = header.Value.ToArray();
        }

        if (responseMessage.Content != null)
        {
            // Copy content headers
            foreach (var header in responseMessage.Content.Headers)
            {
                response.Headers[header.Key] = header.Value.ToArray();
            }

            await responseMessage.Content.CopyToAsync(response.Body).ConfigureAwait(false);
        }
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

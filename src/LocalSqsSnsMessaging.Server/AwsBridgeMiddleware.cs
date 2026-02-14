using System.Buffers;
using System.Text;
using LocalSqsSnsMessaging.Http.Handlers;
using Microsoft.AspNetCore.Http;

namespace LocalSqsSnsMessaging.Server;

/// <summary>
/// ASP.NET Core middleware that routes Kestrel requests directly to the
/// in-memory AWS protocol handlers (SqsOperationHandler/SnsOperationHandler)
/// using their HttpContext-native overloads, avoiding HttpRequestMessage marshalling.
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

            try
            {
                context.Response.Headers["x-amzn-RequestId"] = Guid.NewGuid().ToString();

                if (IsSqsRequest(context.Request))
                {
                    var operationName = ExtractSqsOperationName(context.Request);
                    await SqsOperationHandler.HandleAsync(
                        context, bodyBuffer, bodyLength, operationName, _bus, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var operationName = ExtractSnsOperationName(context.Request, bodyBuffer, bodyLength);
                    await SnsOperationHandler.HandleAsync(
                        context, bodyBuffer, bodyLength, operationName, _bus, cancellationToken).ConfigureAwait(false);
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

    private static async Task WriteErrorResponseAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
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
            Amazon.SQS.Model.QueueDoesNotExistException => 400,
            Amazon.SimpleNotificationService.Model.NotFoundException => 404,
            Amazon.Runtime.AmazonServiceException => 400,
            _ => 500
        };

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/x-amz-json-1.0";
        var errorBytes = Encoding.UTF8.GetBytes(errorJson);
        await context.Response.Body.WriteAsync(errorBytes, cancellationToken).ConfigureAwait(false);
    }
}

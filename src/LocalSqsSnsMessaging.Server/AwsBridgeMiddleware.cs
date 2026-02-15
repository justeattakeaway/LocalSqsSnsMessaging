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

    private readonly BusRegistry _registry;

    public AwsBridgeMiddleware(BusRegistry registry)
    {
        _registry = registry;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var cancellationToken = context.RequestAborted;

        var bus = ResolveBus(context.Request);

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
                        context, bodyBuffer, bodyLength, operationName, bus, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var operationName = ExtractSnsOperationName(context.Request, bodyBuffer, bodyLength);
                    await SnsOperationHandler.HandleAsync(
                        context, bodyBuffer, bodyLength, operationName, bus, cancellationToken).ConfigureAwait(false);
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

    private InMemoryAwsBus ResolveBus(HttpRequest request)
    {
        var accessKey = ExtractAccessKey(request);
        if (accessKey is not null && IsAccountId(accessKey))
        {
            return _registry.GetOrCreate(accessKey);
        }

        return _registry.DefaultBus;
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

    private static bool IsAccountId(string accessKey)
    {
        if (accessKey.Length != 12)
        {
            return false;
        }

        foreach (var c in accessKey)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
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
            NotFoundException => 404,
            AwsServiceException awsEx => (int)awsEx.StatusCode,
            _ => 500
        };

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/x-amz-json-1.0";
        var errorBytes = Encoding.UTF8.GetBytes(errorJson);
        await context.Response.Body.WriteAsync(errorBytes, cancellationToken).ConfigureAwait(false);
    }
}

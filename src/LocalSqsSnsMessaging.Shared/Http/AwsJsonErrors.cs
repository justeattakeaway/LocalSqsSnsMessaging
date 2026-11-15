using System.Buffers;
using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace LocalSqsSnsMessaging.Http;

/// <summary>
/// How SQS and EventBridge, which speak AWS's JSON protocol, answer a failed request:
/// <c>{"__type": "namespace#Code", "message": "..."}</c>.
/// </summary>
internal static class AwsJsonErrors
{
    /// <summary>The namespace of the errors the service framework raises before a service sees the request.</summary>
    private const string ServiceFrameworkNamespace = "com.amazon.coral.service";

    /// <summary>
    /// Writes the error document for <paramref name="exception"/>: an <see cref="AwsServiceException"/>
    /// keeps its code, message and status, anything else is an <c>InternalFailure</c> (HTTP 500).
    /// </summary>
    /// <param name="errorNamespace">The service's own error namespace, e.g. <c>com.amazonaws.sqs</c>.</param>
    /// <param name="queryCompatible">
    /// True for a service that is <c>awsQueryCompatible</c> (SQS): the answer also carries the
    /// <c>x-amzn-query-error</c> header SDKs read the Query protocol's error code from.
    /// </param>
    public static WireResponse Write(
        IBufferWriter<byte> output, Exception exception, string errorNamespace, string contentType, bool queryCompatible)
    {
        string code;
        string message;
        HttpStatusCode status;
        if (exception is AwsServiceException serviceException)
        {
            code = serviceException.ErrorCode ?? exception.GetType().Name.Replace("Exception", "", StringComparison.Ordinal);
            message = serviceException.Message;
            status = serviceException.StatusCode;
        }
        else
        {
            code = "InternalFailure";
            message = exception.Message;
            status = HttpStatusCode.InternalServerError;
        }

        return Write(output, code, message, status, errorNamespace, contentType, queryCompatible);
    }

    /// <summary>Writes the refusal of an operation the service does not have (HTTP 400).</summary>
    public static WireResponse UnknownOperation(IBufferWriter<byte> output, string errorNamespace, string contentType, bool queryCompatible) =>
        Write(output, "UnknownOperationException", string.Empty, HttpStatusCode.BadRequest, errorNamespace, contentType, queryCompatible);

    private static WireResponse Write(
        IBufferWriter<byte> output, string code, string message, HttpStatusCode status, string errorNamespace, string contentType, bool queryCompatible)
    {
        // A message may hold quotes, backslashes and newlines (EventBridge quotes the pattern it refuses).
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteString("__type", $"{(IsServiceFrameworkError(code) ? ServiceFrameworkNamespace : errorNamespace)}#{code}");
            if (message.Length > 0)
            {
                writer.WriteString("message", message);
            }

            writer.WriteEndObject();
        }

        return new WireResponse(
            status,
            contentType,
            WireResponse.NewRequestId(),
            queryError: queryCompatible ? SqsQueryCompatibleErrors.HeaderValue(code, status) : null);
    }

    private static bool IsServiceFrameworkError(string code) =>
        code is "UnknownOperationException" or "SerializationException" or "InternalFailure";
}

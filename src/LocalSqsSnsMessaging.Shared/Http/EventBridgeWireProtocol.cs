using System.Buffers;
using System.Net;
#if NETSTANDARD2_0
using LocalSqsSnsMessaging.Http.Internal;
#endif

namespace LocalSqsSnsMessaging.Http;

/// <summary>
/// EventBridge's wire protocol, AWS JSON 1.1: the operation named by an
/// <c>X-Amz-Target: AWSEvents.&lt;Operation&gt;</c> header, JSON request and response bodies, and
/// every refusal as a JSON error document. The library and the server both answer EventBridge
/// requests through it.
/// </summary>
internal static partial class EventBridgeWireProtocol
{
    /// <summary>The <c>X-Amz-Target</c> prefix of EventBridge's operations.</summary>
    public const string TargetPrefix = "AWSEvents";

    /// <summary>The Content-Type of every EventBridge request and response.</summary>
    public const string ContentType = "application/x-amz-json-1.1";

    private const string ErrorNamespace = "com.amazonaws.events";

    /// <summary>
    /// Runs <paramref name="operation"/> (null when the request named none) with its JSON
    /// <paramref name="body"/> on <paramref name="client"/> (built for the request's region), and
    /// writes the JSON response, or the error document, to <paramref name="output"/>. Only
    /// cancellation escapes: every failure is answered in the protocol.
    /// </summary>
    public static async ValueTask<WireResponse> HandleAsync(
        string? operation,
        ReadOnlyMemory<byte> body,
        InternalEventBridgeClient client,
        ArrayBufferWriter<byte> output,
        CancellationToken cancellationToken)
    {
        if (operation is null)
        {
            return AwsJsonErrors.UnknownOperation(output, ErrorNamespace, ContentType, queryCompatible: false);
        }

#if NET
        using var activity = WireTracing.Start("EventBridge", operation);
#endif
        WireResponse response;
        Exception? failure = null;
        try
        {
            response = await TryDispatchAsync(operation, body, client, output, cancellationToken).ConfigureAwait(false)
                ? new WireResponse(HttpStatusCode.OK, ContentType, WireResponse.NewRequestId())
                : AwsJsonErrors.UnknownOperation(output, ErrorNamespace, ContentType, queryCompatible: false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // Every failure is answered in the protocol.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            failure = ex;
            output.ResetWrittenCount();
            response = AwsJsonErrors.Write(output, ex, ErrorNamespace, ContentType, queryCompatible: false);
        }

#if NET
        WireTracing.Complete(activity, response.Status, failure);
#endif
        return response;
    }

    /// <summary>The operation an <c>X-Amz-Target</c> names, or null if it names no EventBridge operation.</summary>
    public static string? OperationFromTarget(string? target)
    {
        if (target is null || target.Length <= TargetPrefix.Length + 1
            || !target.StartsWith(TargetPrefix, StringComparison.Ordinal) || target[TargetPrefix.Length] != '.')
        {
            return null;
        }

        return target.Substring(TargetPrefix.Length + 1);
    }
}

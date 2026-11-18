using System.Buffers;
using System.Net;
using System.Text;
#if NETSTANDARD2_0
using InMemoryAws.Http.Internal;
#endif

namespace InMemoryAws.Http;

/// <summary>
/// SNS's wire protocol, AWS Query: form-encoded parameters that name the operation in
/// <c>Action</c>, an XML response, and every refusal as an <c>ErrorResponse</c> document. The
/// library and the server both answer SNS requests through it.
/// </summary>
internal static partial class SnsWireProtocol
{
    /// <summary>The Content-Type of every SNS response.</summary>
    public const string ContentType = "text/xml; charset=utf-8";

    private const string XmlNamespace = "http://sns.amazonaws.com/doc/2010-03-31/";

    /// <summary>
    /// Runs the SNS request whose form-encoded <paramref name="parameters"/> (a POST body, or a GET's
    /// query string) name its operation, on <paramref name="client"/> (built for the request's
    /// region), and writes the XML response, or the error document, to <paramref name="output"/>.
    /// Only cancellation escapes: every failure is answered in the protocol.
    /// </summary>
    public static async ValueTask<WireResponse> HandleAsync(
        ReadOnlyMemory<byte> parameters,
        InternalSnsClient client,
        ArrayBufferWriter<byte> output,
        CancellationToken cancellationToken)
    {
        var action = QueryStringParser.GetValue(parameters.Span, "Action");
        if (action is null || action.Length == 0)
        {
            return Error(output, "MissingAction", "No action was supplied with this request.", HttpStatusCode.BadRequest);
        }

#if NET
        using var activity = WireTracing.Start("SNS", action);
#endif
        WireResponse response;
        Exception? failure = null;
        try
        {
            response = await TryDispatchAsync(action, parameters, client, output, cancellationToken).ConfigureAwait(false)
                ? new WireResponse(HttpStatusCode.OK, ContentType, WireResponse.NewRequestId())
                : Error(output, "InvalidAction", $"The action {action} is not valid for this endpoint.", HttpStatusCode.BadRequest);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AwsServiceException ex)
        {
            failure = ex;
            output.ResetWrittenCount();
            response = Error(output, ex.ErrorCode ?? ex.GetType().Name.Replace("Exception", "", StringComparison.Ordinal), ex.Message, ex.StatusCode);
        }
#pragma warning disable CA1031 // Any other failure is an internal error, answered as SNS would.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            failure = ex;
            output.ResetWrittenCount();
            response = Error(output, "InternalFailure", ex.Message, HttpStatusCode.InternalServerError);
        }

#if NET
        WireTracing.Complete(activity, response.Status, failure);
#endif
        return response;
    }

    /// <summary>Writes an <c>ErrorResponse</c> document to <paramref name="output"/>.</summary>
    private static WireResponse Error(IBufferWriter<byte> output, string code, string message, HttpStatusCode status)
    {
        var requestId = WireResponse.NewRequestId();
        var xml = $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <ErrorResponse xmlns="{XmlNamespace}">
            <Error>
                <Type>{((int)status >= 500 ? "Receiver" : "Sender")}</Type>
                <Code>{System.Security.SecurityElement.Escape(code)}</Code>
                <Message>{System.Security.SecurityElement.Escape(message)}</Message>
            </Error>
            <RequestId>{requestId}</RequestId>
        </ErrorResponse>
        """;
        output.Write(Encoding.UTF8.GetBytes(xml));
        return new WireResponse(status, ContentType, requestId);
    }
}

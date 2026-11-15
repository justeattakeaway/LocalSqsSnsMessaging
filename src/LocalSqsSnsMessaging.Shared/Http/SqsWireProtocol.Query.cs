using System.Buffers;
using System.Net;
using System.Xml;
using LocalSqsSnsMessaging.Http.Handlers;
#if NETSTANDARD2_0
using LocalSqsSnsMessaging.Http.Internal;
#endif

namespace LocalSqsSnsMessaging.Http;

/// <summary>
/// SQS over the AWS Query protocol: form-encoded <c>Action=...</c> parameters in, an XML
/// <c>&lt;Action&gt;Response</c> or <c>ErrorResponse</c> document out. The operations themselves
/// are the same <see cref="InternalSqsClient"/> calls the JSON protocol makes, so both protocols
/// share one queue state.
/// </summary>
internal static partial class SqsWireProtocol
{
    /// <summary>The Content-Type of every Query response.</summary>
    public const string QueryContentType = "text/xml";

    /// <summary>
    /// Over Query, SQS words its missing-queue error differently from JSON's
    /// <see cref="InternalQueueDoesNotExistException.DefaultMessage"/>.
    /// </summary>
    private const string QueryQueueDoesNotExistMessage = "The specified queue does not exist for this wsdl version.";

    /// <summary>
    /// Runs the request in <paramref name="parameters"/> on <paramref name="client"/> (built for the
    /// request's region and caller, as for the JSON protocol) and writes its XML response, or the
    /// <c>ErrorResponse</c> document, to <paramref name="output"/>. Only cancellation escapes: every
    /// failure is answered in the protocol.
    /// </summary>
    public static async ValueTask<WireResponse> HandleQueryAsync(
        Dictionary<string, string> parameters,
        InternalSqsClient client,
        ArrayBufferWriter<byte> output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(output);

        if (!parameters.TryGetValue("Action", out var action) || action is null || action.Length == 0)
        {
            return WriteQueryError(output, "MissingAction", "No action was supplied with this request.", HttpStatusCode.BadRequest);
        }

#if NET
        using var activity = WireTracing.Start("SQS", action);
#endif
        WireResponse response;
        Exception? failure = null;
        try
        {
            response = await TryDispatchQueryAsync(action, parameters, client, output, cancellationToken).ConfigureAwait(false)
                ? new WireResponse(HttpStatusCode.OK, QueryContentType, WireResponse.NewRequestId())
                : WriteQueryError(output, "InvalidAction", $"The action {action} is not valid for this endpoint.", HttpStatusCode.BadRequest);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AwsServiceException ex)
        {
            // A failure part-way through a response leaves no half success document ahead of the error.
            failure = ex;
            output.ResetWrittenCount();
            var code = ex.ErrorCode ?? ex.GetType().Name.Replace("Exception", "", StringComparison.Ordinal);
            var message = string.Equals(code, "QueueDoesNotExist", StringComparison.Ordinal)
                          && string.Equals(ex.Message, InternalQueueDoesNotExistException.DefaultMessage, StringComparison.Ordinal)
                ? QueryQueueDoesNotExistMessage
                : ex.Message;
            response = WriteQueryError(output, SqsQueryCompatibleErrors.QueryCode(code), message, ex.StatusCode);
        }
#pragma warning disable CA1031 // Any other failure is an internal error, answered as SQS would.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            failure = ex;
            output.ResetWrittenCount();
            response = WriteQueryError(output, "InternalFailure", ex.Message, HttpStatusCode.InternalServerError);
        }

#if NET
        WireTracing.Complete(activity, response.Status, failure);
#endif
        return response;
    }

    /// <summary>Writes a Query <c>ErrorResponse</c> document.</summary>
    private static WireResponse WriteQueryError(IBufferWriter<byte> output, string code, string message, HttpStatusCode statusCode)
    {
        var requestId = WireResponse.NewRequestId();
        using var stream = new BufferWriterStream(output);
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new System.Text.UTF8Encoding(false), CloseOutput = false, NewLineHandling = NewLineHandling.Entitize }))
        {
            writer.WriteStartElement("ErrorResponse", SqsQuerySerializers.XmlNamespace);
            writer.WriteStartElement("Error");
            writer.WriteElementString("Type", (int)statusCode >= 500 ? "Receiver" : "Sender");
            writer.WriteElementString("Code", code);
            writer.WriteElementString("Message", message);
            writer.WriteStartElement("Detail");
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteElementString("RequestId", requestId);
            writer.WriteEndElement();
        }

        return new WireResponse(statusCode, QueryContentType, requestId);
    }
}

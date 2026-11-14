using System.Buffers;
using System.Net;
using System.Xml;

namespace LocalSqsSnsMessaging.Http.Handlers;

/// <summary>
/// Serves SQS over the AWS Query protocol: form-encoded <c>Action=...</c> parameters in, an XML
/// <c>&lt;Action&gt;Response</c> or <c>ErrorResponse</c> document out. The operations themselves
/// are the same <see cref="InternalSqsClient"/> calls the JSON protocol makes, so both protocols
/// share one queue state.
/// </summary>
internal static partial class SqsQueryOperationHandler
{
    /// <summary>The Content-Type of every Query response.</summary>
    public const string ContentType = "text/xml";

    /// <summary>
    /// Over Query, SQS words its missing-queue error differently from JSON's
    /// <see cref="InternalQueueDoesNotExistException.DefaultMessage"/>.
    /// </summary>
    private const string QueryQueueDoesNotExistMessage = "The specified queue does not exist for this wsdl version.";

    /// <summary>
    /// Runs the request in <paramref name="parameters"/> and writes its XML response to
    /// <paramref name="output"/>, returning the HTTP status code to send it with.
    /// </summary>
    public static async Task<HttpStatusCode> HandleAsync(
        Dictionary<string, string> parameters,
        InMemoryAwsBus bus,
        IBufferWriter<byte> output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(output);

        if (!parameters.TryGetValue("Action", out var action) || string.IsNullOrEmpty(action))
        {
            return WriteError(output, "MissingAction", "No action was supplied with this request.", HttpStatusCode.BadRequest);
        }

        // Serialize into a scratch buffer, so that a failure part-way through a response never
        // leaves half a success document ahead of the error document.
        using var pooled = PooledArrayBufferWriter.Rent();
        try
        {
            if (!await TryDispatchAsync(action, parameters, new InternalSqsClient(bus), pooled.Writer, cancellationToken).ConfigureAwait(false))
            {
                return WriteError(output, "InvalidAction", $"The action {action} is not valid for this endpoint.", HttpStatusCode.BadRequest);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AwsServiceException ex)
        {
            var code = ex.ErrorCode ?? ex.GetType().Name.Replace("Exception", "", StringComparison.Ordinal);
            var message = string.Equals(code, "QueueDoesNotExist", StringComparison.Ordinal)
                          && string.Equals(ex.Message, InternalQueueDoesNotExistException.DefaultMessage, StringComparison.Ordinal)
                ? QueryQueueDoesNotExistMessage
                : ex.Message;
            return WriteError(output, SqsQueryCompatibleErrors.QueryCode(code), message, ex.StatusCode);
        }
#pragma warning disable CA1031 // Any other failure is an internal error, answered as SQS would.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return WriteError(output, "InternalFailure", ex.Message, HttpStatusCode.InternalServerError);
        }

        output.Write(pooled.Writer.WrittenSpan);
        return HttpStatusCode.OK;
    }

    /// <summary>Writes a Query <c>ErrorResponse</c> document, returning <paramref name="statusCode"/>.</summary>
    private static HttpStatusCode WriteError(IBufferWriter<byte> output, string code, string message, HttpStatusCode statusCode)
    {
        using var stream = new BufferWriterStream(output);
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new System.Text.UTF8Encoding(false), CloseOutput = false }))
        {
            writer.WriteStartElement("ErrorResponse", SqsQuerySerializers.XmlNamespace);
            writer.WriteStartElement("Error");
            writer.WriteElementString("Type", (int)statusCode >= 500 ? "Receiver" : "Sender");
            writer.WriteElementString("Code", code);
            writer.WriteElementString("Message", message);
            writer.WriteStartElement("Detail");
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteElementString("RequestId", Guid.NewGuid().ToString());
            writer.WriteEndElement();
        }

        return statusCode;
    }
}

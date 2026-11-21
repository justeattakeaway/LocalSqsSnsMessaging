using System.Buffers;
using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using InMemoryAws.DynamoDb;
using InMemoryAws.Http.Handlers;
#if NETSTANDARD2_0
using InMemoryAws.Http.Internal;
#endif

namespace InMemoryAws.Http;

/// <summary>
/// DynamoDB and DynamoDB Streams over the JSON 1.0 protocol: a POST with an <c>X-Amz-Target</c> of
/// <c>DynamoDB_20120810.Operation</c> (or <c>DynamoDBStreams_20120810.Operation</c>) and a JSON body.
/// Transport-neutral, so the library's HTTP handler and the server answer alike. Every response,
/// error or not, carries the <c>x-amz-crc32</c> header SDKs check.
/// </summary>
internal static class DynamoDbWireProtocol
{
    public const string ContentType = "application/x-amz-json-1.0";
    public const string DynamoDbTargetPrefix = "DynamoDB_20120810";
    public const string StreamsTargetPrefix = "DynamoDBStreams_20120810";

    /// <summary>
    /// Deep enough for an item nested past DynamoDB's 32 levels (each level is two JSON levels), so
    /// DynamoDB's own nesting refusal is what such an item gets.
    /// </summary>
    private static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = 512 };

    /// <summary>
    /// DynamoDB writes text as UTF-8, escaping only what JSON requires, so a response's bytes (and
    /// the <c>x-amz-crc32</c> over them) carry its strings as stored. The relaxed encoder comes
    /// closest: it still escapes characters outside the Basic Multilingual Plane.
    /// </summary>
    private static readonly JavaScriptEncoder ResponseEncoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

    /// <summary>Whether <paramref name="target"/> is a DynamoDB or DynamoDB Streams <c>X-Amz-Target</c>.</summary>
    public static bool IsTarget(string? target) =>
        target is not null
        && (target.StartsWith(DynamoDbTargetPrefix + ".", StringComparison.Ordinal)
            || target.StartsWith(StreamsTargetPrefix + ".", StringComparison.Ordinal));

    /// <summary>
    /// The refusal of a request that signs two ways at once, with an <c>Authorization</c> header and
    /// presigned-URL (<c>X-Amz-Algorithm</c>) query parameters: DynamoDB answers it before reading the
    /// request, with no message; null for any other request.
    /// </summary>
    public static WireResponse? ConflictingSignatures(bool hasAuthorizationHeader, bool hasAlgorithmQuery, ArrayBufferWriter<byte> output) =>
        hasAuthorizationHeader && hasAlgorithmQuery
            ? Error(output, "InvalidSignatureException", string.Empty, HttpStatusCode.BadRequest, null)
            : null;

    /// <summary>
    /// Serves one request for <paramref name="region"/> of <paramref name="cloud"/>, writing the JSON
    /// response, or the error document, to <paramref name="output"/>. Only cancellation escapes.
    /// </summary>
    /// <remarks>
    /// DynamoDB and DynamoDB Streams share this one protocol, and the target names which of the two
    /// a request is for, so this builds the client itself.
    /// </remarks>
    public static async ValueTask<WireResponse> HandleAsync(
        string? target,
        ReadOnlyMemory<byte> body,
        InMemoryAwsCloud cloud,
        string? region,
        ArrayBufferWriter<byte> output,
        CancellationToken cancellationToken)
    {
        var dot = target?.IndexOf('.', StringComparison.Ordinal) ?? -1;
        var service = dot > 0 ? target![..dot] : string.Empty;
        var operation = dot > 0 ? target![(dot + 1)..] : string.Empty;
        var isStreams = string.Equals(service, StreamsTargetPrefix, StringComparison.Ordinal);
        var isOperation = isStreams
            ? DynamoDbStreamsOperationDispatcher.IsOperation(operation)
            : string.Equals(service, DynamoDbTargetPrefix, StringComparison.Ordinal) && DynamoDbOperationDispatcher.IsOperation(operation);

#if NET
        using var activity = isOperation ? WireTracing.Start(isStreams ? "DynamoDBStreams" : "DynamoDB", operation) : null;
#endif
        WireResponse response;
        Exception? failure = null;
        try
        {
            if (!isOperation)
            {
                throw new InternalUnknownOperationException();
            }

            JsonDocument document;
            try
            {
                document = body.Length == 0 ? JsonDocument.Parse("{}") : JsonDocument.Parse(body, DocumentOptions);
            }
            catch (JsonException)
            {
                throw new InternalSerializationException("Start of structure or map found where not expected.");
            }

            using (document)
            using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { SkipValidation = true, Encoder = ResponseEncoder }))
            {
                if (isStreams)
                {
                    await DynamoDbStreamsOperationDispatcher.DispatchAsync(
                        operation, document.RootElement, new InternalDynamoDbStreamsClient(cloud, region), writer, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await DynamoDbOperationDispatcher.DispatchAsync(
                        operation, document.RootElement, new InternalDynamoDbClient(cloud, region), writer, cancellationToken).ConfigureAwait(false);
                }
            }

            response = Answer(output, HttpStatusCode.OK);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AwsServiceException ex) when (IsMissingTableName(target, ex))
        {
            // DynamoDB finds an absent TableName itself, before the model's constraints are walked,
            // and says so in its own sentence (DeleteTable alone answers with the model's clause).
            failure = ex;
            response = Error(output, "ValidationException", "The parameter 'TableName' is required but was not present in the request", HttpStatusCode.BadRequest, null);
        }
        catch (AwsServiceException ex) when (BareReturnConsumedCapacityClause(target, ex) is { } clause)
        {
            // UpdateItem checks ReturnConsumedCapacity itself and states the clause alone.
            failure = ex;
            response = Error(output, "ValidationException", clause, HttpStatusCode.BadRequest, null);
        }
        catch (AwsServiceException ex)
        {
            failure = ex;
            response = Error(output, ex.ErrorCode ?? "InternalServerError", ex.Message, ex.StatusCode, ex as IAwsJsonErrorMembers);
        }
        catch (NotSupportedException ex)
        {
            // An operation the cloud doesn't emulate (S3 exports, Kinesis destinations, ...).
            failure = ex;
            response = Error(output, "UnknownOperationException", ex.Message, HttpStatusCode.BadRequest, null);
        }
#pragma warning disable CA1031 // Any other failure is an internal error, answered in the protocol.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            failure = ex;
            response = Error(output, "InternalServerError", ex.Message, HttpStatusCode.InternalServerError, null);
        }

#if NET
        WireTracing.Complete(activity, response.Status, failure);
#endif
        return response;
    }

    private static string? BareReturnConsumedCapacityClause(string? target, AwsServiceException ex)
    {
        const string Envelope = "1 validation error detected: ";
        return ex is InternalValidationException
            && string.Equals(target, DynamoDbTargetPrefix + ".UpdateItem", StringComparison.Ordinal)
            && ex.Message.StartsWith(Envelope + "Value '", StringComparison.Ordinal)
            && ex.Message.Contains("' at 'returnConsumedCapacity' failed to satisfy constraint: ", StringComparison.Ordinal)
            ? ex.Message[Envelope.Length..]
            : null;
    }

    private static bool IsMissingTableName(string? target, AwsServiceException ex) =>
        ex is InternalValidationException
        && target is not null
        && target.StartsWith(DynamoDbTargetPrefix + ".", StringComparison.Ordinal)
        && !string.Equals(target, DynamoDbTargetPrefix + ".DeleteTable", StringComparison.Ordinal)
        && ex.Message.Contains(AwsValidation.Missing("tableName"), StringComparison.Ordinal);

    /// <summary>Writes an error document over whatever <paramref name="output"/> holds so far.</summary>
    private static WireResponse Error(ArrayBufferWriter<byte> output, string code, string message, HttpStatusCode status, IAwsJsonErrorMembers? members)
    {
        output.ResetWrittenCount();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Encoder = ResponseEncoder }))
        {
            writer.WriteStartObject();
            writer.WriteString("__type", $"{ErrorNamespace(code)}#{code}");
            if (message.Length > 0)
            {
                // The service framework's SerializationException names its member as coral does.
                writer.WriteString(code == "SerializationException" ? "Message" : "message", message);
            }

            members?.WriteMembers(writer);
            writer.WriteEndObject();
        }

        return Answer(output, status);
    }

    /// <summary>The answer for the body <paramref name="output"/> holds, with that body's CRC32.</summary>
    private static WireResponse Answer(ArrayBufferWriter<byte> output, HttpStatusCode status) =>
        new(status, ContentType, WireResponse.NewDynamoDbRequestId(), crc32: Http.Crc32.Compute(output.WrittenSpan).ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// The namespace DynamoDB's <c>__type</c> names an error in: its request validation and
    /// protocol errors come from the service framework, the rest from DynamoDB itself.
    /// </summary>
    private static string ErrorNamespace(string code) => code switch
    {
        "ValidationException" => "com.amazon.coral.validate",
        "SerializationException" or "UnknownOperationException" or "AccessDeniedException"
            or "MissingAuthenticationTokenException" or "UnrecognizedClientException"
            or "IncompleteSignatureException" or "InvalidSignatureException" => "com.amazon.coral.service",
        _ => "com.amazonaws.dynamodb.v20120810",
    };
}

/// <summary>The CRC-32 (IEEE 802.3) checksum, which DynamoDB sends as <c>x-amz-crc32</c>.</summary>
internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            }

            table[i] = value;
        }

        return table;
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
    }
}

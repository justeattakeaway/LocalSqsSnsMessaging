using System.Buffers;
using System.Net;
using System.Text.Json;
using LocalSqsSnsMessaging.DynamoDb;
using LocalSqsSnsMessaging.Http.Handlers;
#if NETSTANDARD2_0
using LocalSqsSnsMessaging.Http.Internal;
#endif

namespace LocalSqsSnsMessaging.Http;

/// <summary>A DynamoDB response, ready to send: its status, JSON body and the body's CRC32.</summary>
internal sealed class DynamoDbWireResponse(HttpStatusCode status, byte[] body)
{
    public HttpStatusCode Status { get; } = status;

    public byte[] Body { get; } = body;

    /// <summary>The <c>x-amz-crc32</c> header DynamoDB sends with every response, which SDKs check.</summary>
    public string Crc32 { get; } = Http.Crc32.Compute(body).ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// DynamoDB and DynamoDB Streams over the JSON 1.0 protocol: a POST with an <c>X-Amz-Target</c> of
/// <c>DynamoDB_20120810.Operation</c> (or <c>DynamoDBStreams_20120810.Operation</c>) and a JSON body.
/// Transport-neutral, so the library's HTTP handler and the server answer alike.
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
    public static DynamoDbWireResponse? ConflictingSignatures(bool hasAuthorizationHeader, bool hasAlgorithmQuery) =>
        hasAuthorizationHeader && hasAlgorithmQuery
            ? Error("InvalidSignatureException", string.Empty, HttpStatusCode.BadRequest, null)
            : null;

    /// <summary>Serves one request for <paramref name="region"/> of <paramref name="bus"/>.</summary>
    public static async Task<DynamoDbWireResponse> HandleAsync(
        string? target,
        ReadOnlyMemory<byte> body,
        InMemoryAwsBus bus,
        string? region,
        CancellationToken cancellationToken)
    {
        var buffer = new ArrayBufferWriter<byte>();
        try
        {
            var dot = target?.IndexOf('.', StringComparison.Ordinal) ?? -1;
            var service = dot > 0 ? target![..dot] : string.Empty;
            var operation = dot > 0 ? target![(dot + 1)..] : string.Empty;
            var isStreams = string.Equals(service, StreamsTargetPrefix, StringComparison.Ordinal);
            if (!(isStreams ? DynamoDbStreamsOperationDispatcher.IsOperation(operation)
                    : string.Equals(service, DynamoDbTargetPrefix, StringComparison.Ordinal) && DynamoDbOperationDispatcher.IsOperation(operation)))
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
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { SkipValidation = true }))
            {
#if NET
                using var activity = InMemoryAwsBus.ActivitySource.StartActivity(
                    $"{(isStreams ? "DynamoDBStreams" : "DynamoDB")}.{operation}", System.Diagnostics.ActivityKind.Client);
#endif
                if (isStreams)
                {
                    await DynamoDbStreamsOperationDispatcher.DispatchAsync(
                        operation, document.RootElement, new InternalDynamoDbStreamsClient(bus, region), writer, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await DynamoDbOperationDispatcher.DispatchAsync(
                        operation, document.RootElement, new InternalDynamoDbClient(bus, region), writer, cancellationToken).ConfigureAwait(false);
                }
            }

            return new DynamoDbWireResponse(HttpStatusCode.OK, buffer.WrittenSpan.ToArray());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AwsServiceException ex) when (IsMissingTableName(target, ex))
        {
            // DynamoDB finds an absent TableName itself, before the model's constraints are walked,
            // and says so in its own sentence (DeleteTable alone answers with the model's clause).
            return Error("ValidationException", "The parameter 'TableName' is required but was not present in the request", HttpStatusCode.BadRequest, null);
        }
        catch (AwsServiceException ex) when (BareReturnConsumedCapacityClause(target, ex) is { } clause)
        {
            // UpdateItem checks ReturnConsumedCapacity itself and states the clause alone.
            return Error("ValidationException", clause, HttpStatusCode.BadRequest, null);
        }
        catch (AwsServiceException ex)
        {
            return Error(ex.ErrorCode ?? "InternalServerError", ex.Message, ex.StatusCode, ex as IAwsJsonErrorMembers);
        }
        catch (NotSupportedException ex)
        {
            // An operation the bus doesn't emulate (S3 exports, Kinesis destinations, ...).
            return Error("UnknownOperationException", ex.Message, HttpStatusCode.BadRequest, null);
        }
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

    private static DynamoDbWireResponse Error(string code, string message, HttpStatusCode status, IAwsJsonErrorMembers? members)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("__type", $"{ErrorNamespace(code)}#{code}");
            if (message.Length > 0)
            {
                writer.WriteString("message", message);
            }

            members?.WriteMembers(writer);
            writer.WriteEndObject();
        }

        return new DynamoDbWireResponse(status, buffer.WrittenSpan.ToArray());
    }

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

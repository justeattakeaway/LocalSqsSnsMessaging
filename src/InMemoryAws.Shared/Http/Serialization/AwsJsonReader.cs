using System.Text.Json;

namespace InMemoryAws.Http.Serialization;

/// <summary>
/// Reads the members of an AWS JSON-protocol request for the shape-per-method serializers. A member
/// of the wrong JSON type is refused with <c>SerializationException</c>, as AWS's JSON services
/// refuse it, before any validation runs.
/// </summary>
internal static class AwsJsonReader
{
    public static void ExpectObject(JsonElement element, string shape)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InternalSerializationException($"Unexpected value type in payload: expected {shape}");
        }
    }

    /// <summary>
    /// A map member, as DynamoDB's service framework reads one: a scalar is an "Unexpected field
    /// type", and an array is refused with no message at all.
    /// </summary>
    public static void ExpectMap(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                return;
            case JsonValueKind.Array:
                throw new InternalSerializationException(string.Empty);
            default:
                throw new InternalSerializationException("Unexpected field type");
        }
    }

    public static void ExpectArray(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new InternalSerializationException("Unexpected value type in payload: expected a list");
        }
    }

    /// <summary>
    /// A string member. Any other JSON value is refused as DynamoDB's Jackson-based reader words it,
    /// naming the token it found; an array gets no message at all.
    /// </summary>
    public static string ReadString(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString()!,
        JsonValueKind.Number => throw new InternalSerializationException(
            IsInteger(element) ? "NUMBER_VALUE cannot be converted to String" : "DECIMAL_VALUE cannot be converted to String"),
        JsonValueKind.True => throw new InternalSerializationException("TRUE_VALUE cannot be converted to String"),
        JsonValueKind.False => throw new InternalSerializationException("FALSE_VALUE cannot be converted to String"),
        JsonValueKind.Object => throw new InternalSerializationException("Start of structure or map found where not expected"),
        _ => throw new InternalSerializationException(string.Empty),
    };

    /// <summary>A number written without a fraction or exponent: Jackson's <c>VALUE_NUMBER_INT</c>.</summary>
    private static bool IsInteger(JsonElement element) => element.GetRawText().IndexOfAny(['.', 'e', 'E']) < 0;

    public static int ReadInt32(JsonElement element) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value)
            ? value
            : throw new InternalSerializationException("Unexpected value type in payload: expected an integer");

    public static long ReadInt64(JsonElement element) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var value)
            ? value
            : throw new InternalSerializationException("Unexpected value type in payload: expected a long");

    public static double ReadDouble(JsonElement element) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var value)
            ? value
            : throw new InternalSerializationException("Unexpected value type in payload: expected a number");

    public static bool ReadBoolean(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new InternalSerializationException("Unexpected value type in payload: expected a boolean"),
    };

    public static DateTime ReadTimestamp(JsonElement element) =>
        AwsJsonTimestamp.TryRead(element, out var value)
            ? value
            : throw new InternalSerializationException("Unexpected value type in payload: expected a timestamp");

    public static byte[] ReadBlob(JsonElement element) =>
        element.ValueKind == JsonValueKind.String && element.TryGetBytesFromBase64(out var bytes)
            ? bytes
            : throw new InternalSerializationException("Base64 encoded value is invalid");
}

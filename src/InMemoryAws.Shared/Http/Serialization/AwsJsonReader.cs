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

    public static string ReadString(JsonElement element) =>
        element.ValueKind == JsonValueKind.String
            ? element.GetString()!
            : throw new InternalSerializationException("Unexpected value type in payload: expected a string");

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

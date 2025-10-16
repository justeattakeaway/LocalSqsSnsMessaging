using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalSqsSnsMessaging.Http.Serialization;

/// <summary>
/// JSON converter that handles MemoryStream serialization/deserialization.
/// AWS SDK serializes binary data as base64 strings in JSON.
/// </summary>
internal sealed class MemoryStreamJsonConverter : JsonConverter<MemoryStream>
{
    public override MemoryStream? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            var base64 = reader.GetString();
            if (string.IsNullOrEmpty(base64))
            {
                return new MemoryStream();
            }

            var bytes = Convert.FromBase64String(base64);
            return new MemoryStream(bytes);
        }

        throw new JsonException($"Unexpected token type {reader.TokenType} when deserializing MemoryStream");
    }

    public override void Write(Utf8JsonWriter writer, MemoryStream value, JsonSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNullValue();
            return;
        }

        var bytes = value.ToArray();
        var base64 = Convert.ToBase64String(bytes);
        writer.WriteStringValue(base64);
    }
}

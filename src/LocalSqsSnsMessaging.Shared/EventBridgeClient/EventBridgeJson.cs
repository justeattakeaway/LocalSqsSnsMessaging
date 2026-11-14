using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalSqsSnsMessaging;

/// <summary>
/// JSON reading and writing as EventBridge does it: compact output that escapes only what JSON
/// requires (quotes, backslashes and control characters), so non-ASCII text reaches a target as
/// sent, and numbers written with the digits they were sent with, never rounded through a double.
/// </summary>
internal static class EventBridgeJson
{
    /// <summary>Writes <paramref name="node"/> as compact JSON; a C# <see langword="null"/> is JSON <c>null</c>.</summary>
    public static string Serialize(JsonNode? node)
    {
        var builder = new StringBuilder();
        Write(builder, node);
        return builder.ToString();
    }

    /// <summary>Appends <paramref name="value"/> escaped for use inside a JSON string, without the surrounding quotes.</summary>
    public static void AppendEscaped(StringBuilder builder, string value)
    {
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                case < ' ':
                    builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    break;
                default: builder.Append(c); break;
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="json"/> is one well-formed JSON value. On failure, <paramref name="line"/>
    /// and <paramref name="column"/> are the 1-based position of the first offending character.
    /// </summary>
    public static bool IsValid(string json, out JsonValueKind kind, out long line, out long column)
    {
        try
        {
            // Depth is not what is being validated here: a rendered input may legitimately nest
            // an event whose own detail is already at the detail depth limit.
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = int.MaxValue });
            kind = document.RootElement.ValueKind;
            line = column = 0;
            return true;
        }
        catch (JsonException ex)
        {
            kind = JsonValueKind.Undefined;
            line = (ex.LineNumber ?? 0) + 1;
            column = (ex.BytePositionInLine ?? 0) + 1;
            return false;
        }
    }

    private static void Write(StringBuilder builder, JsonNode? node)
    {
        switch (node)
        {
            case null:
                builder.Append("null");
                break;
            case JsonObject obj:
                builder.Append('{');
                var firstProperty = true;
                foreach (var (name, value) in obj)
                {
                    if (!firstProperty)
                    {
                        builder.Append(',');
                    }
                    firstProperty = false;
                    builder.Append('"');
                    AppendEscaped(builder, name);
                    builder.Append("\":");
                    Write(builder, value);
                }
                builder.Append('}');
                break;
            case JsonArray array:
                builder.Append('[');
                for (var i = 0; i < array.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(',');
                    }
                    Write(builder, array[i]);
                }
                builder.Append(']');
                break;
            default:
                WriteValue(builder, node.AsValue());
                break;
        }
    }

    private static void WriteValue(StringBuilder builder, JsonValue value)
    {
        switch (value.GetValueKind())
        {
            case JsonValueKind.String:
                builder.Append('"');
                AppendEscaped(builder, value.GetValue<string>());
                builder.Append('"');
                break;
            case JsonValueKind.Number when value.TryGetValue<JsonElement>(out var element):
                // A parsed number keeps the exact text it was sent with.
                builder.Append(element.GetRawText());
                break;
            default:
                builder.Append(value.ToJsonString());
                break;
        }
    }
}

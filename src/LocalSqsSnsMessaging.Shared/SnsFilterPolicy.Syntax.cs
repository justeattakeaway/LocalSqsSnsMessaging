using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalSqsSnsMessaging;

internal static partial class SnsFilterPolicy
{
    /// <summary>
    /// Writes <paramref name="node"/> as compact JSON, escaping strings as Jackson does. SNS checks a
    /// policy's rules against this form, so its error locations count columns in it.
    /// </summary>
    private static string WriteCompact(JsonNode? node)
    {
        var builder = new StringBuilder();
        Write(node);
        return builder.ToString();

        void Write(JsonNode? value)
        {
            switch (value)
            {
                case null:
                    builder.Append("null");
                    break;

                case JsonObject obj:
                    builder.Append('{');
                    var firstProperty = true;
                    foreach (var (name, child) in obj)
                    {
                        if (!firstProperty)
                        {
                            builder.Append(',');
                        }
                        firstProperty = false;
                        WriteString(name);
                        builder.Append(':');
                        Write(child);
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
                        Write(array[i]);
                    }
                    builder.Append(']');
                    break;

                default:
                    if (value.GetValueKind() == JsonValueKind.String)
                    {
                        WriteString(value.GetValue<string>());
                    }
                    else
                    {
                        builder.Append(value.ToJsonString());
                    }
                    break;
            }
        }

        void WriteString(string value)
        {
            builder.Append('"');
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '\b':
                        builder.Append("\\b");
                        break;
                    case '\f':
                        builder.Append("\\f");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    case < (char)0x20:
                        builder.Append("\\u00").Append(((int)c).ToString("X2", CultureInfo.InvariantCulture));
                        break;
                    default:
                        builder.Append(c);
                        break;
                }
            }
            builder.Append('"');
        }
    }
}

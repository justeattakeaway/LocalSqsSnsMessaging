using System.Globalization;
using System.Text.Json.Nodes;
using LocalSqsSnsMessaging.EventBridge.Model;

namespace LocalSqsSnsMessaging;

/// <summary>Renders what a target receives for a matched event: Input, InputPath or InputTransformer.</summary>
internal static class EventBridgeTargetInput
{
    /// <summary>What a target receives for an event: its Input, InputPath or InputTransformer, else the whole envelope.</summary>
    public static string Resolve(Target target, JsonObject envelope)
    {
        if (target.Input is not null)
        {
            return target.Input;
        }
        if (target.InputPath is not null)
        {
            return ApplyPath(envelope, target.InputPath) ?? "null";
        }
        if (target.InputTransformer is not null)
        {
            return ApplyTransformer(target.InputTransformer, envelope);
        }
        return envelope.ToJsonString();
    }

    private static string ApplyTransformer(InputTransformer transformer, JsonObject envelope)
    {
        var template = transformer.InputTemplate ?? string.Empty;
        if (transformer.InputPathsMap is not null)
        {
            foreach (var (name, path) in transformer.InputPathsMap)
            {
                var value = ApplyPath(envelope, path);
                template = template.Replace($"<{name}>", value is null ? string.Empty : Unquote(value), StringComparison.Ordinal);
            }
        }
        return template;
    }

    private static string Unquote(string json) =>
        json.Length >= 2 && json[0] == '"' && json[^1] == '"' ? json[1..^1] : json;

    /// <summary>Minimal JSONPath supporting "$", "$.a.b", and "$.a[0]".</summary>
    private static string? ApplyPath(JsonObject envelope, string path)
    {
        if (path == "$")
        {
            return envelope.ToJsonString();
        }

        JsonNode? node = envelope;
        foreach (var raw in path.TrimStart('$').Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = raw;
            int bracket;
            while ((bracket = segment.IndexOf('[', StringComparison.Ordinal)) >= 0)
            {
                var name = segment[..bracket];
                if (name.Length > 0)
                {
                    node = (node as JsonObject)?[name];
                }
                var end = segment.IndexOf(']', StringComparison.Ordinal);
                if (end < 0 || !int.TryParse(segment.AsSpan(bracket + 1, end - bracket - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx))
                {
                    return null;
                }
                node = (node as JsonArray)?[idx];
                segment = segment[(end + 1)..];
            }
            if (segment.Length > 0)
            {
                node = (node as JsonObject)?[segment];
            }
            if (node is null)
            {
                return null;
            }
        }
        return node.ToJsonString();
    }
}

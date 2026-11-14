using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalSqsSnsMessaging;

/// <summary>
/// Parses a document that patterns are matched against: an EventBridge event's <c>Detail</c> or a
/// whole test event, or an SNS message body.
/// </summary>
/// <remarks>
/// Event Ruler's parser (Jackson) accepts a key repeated within one object and keeps its last value.
/// <see cref="JsonNode.Parse(string, JsonNodeOptions?, JsonDocumentOptions)"/> accepts such a
/// document too, but throws <see cref="ArgumentException"/> the first time the object is read. The
/// document is therefore built eagerly, a repeated key keeping its first position and taking its last
/// value, as <see cref="EventRulerJsonReader"/> does for patterns.
/// </remarks>
internal static class EventRulerDocument
{
    /// <summary>
    /// Parses <paramref name="json"/> as a JSON object; <see langword="null"/> when it is not JSON, is
    /// not an object, or nests deeper than <paramref name="maxDepth"/> (0 for the parser's default of 64).
    /// </summary>
    public static JsonObject? ParseObject(string json, int maxDepth = 0)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = maxDepth });
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? BuildObject(document.RootElement.Clone())
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonNode? Build(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => BuildObject(element),
        JsonValueKind.Array => new JsonArray([.. element.EnumerateArray().Select(Build)]),
        JsonValueKind.Null => null,
        _ => JsonValue.Create(element)
    };

    private static JsonObject BuildObject(JsonElement element)
    {
        var obj = new JsonObject();
        foreach (var property in element.EnumerateObject())
        {
            // Setting a key that is already there replaces its value in place.
            obj[property.Name] = Build(property.Value);
        }

        return obj;
    }
}

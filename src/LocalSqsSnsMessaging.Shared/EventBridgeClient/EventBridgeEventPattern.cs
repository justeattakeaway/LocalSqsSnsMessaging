using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalSqsSnsMessaging;

/// <summary>
/// Implements EventBridge content-based event pattern matching on top of <see cref="JsonPatternMatcher"/>.
/// See https://docs.aws.amazon.com/eventbridge/latest/userguide/eb-event-patterns.html
/// </summary>
internal static class EventBridgeEventPattern
{
    /// <summary>Validates that a pattern string is a well-formed event pattern (a JSON object).</summary>
    public static bool IsValid(string? pattern, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(pattern))
        {
            error = "Event pattern is empty.";
            return false;
        }

        try
        {
            if (JsonNode.Parse(pattern!) is not JsonObject)
            {
                error = "Event pattern must be a JSON object.";
                return false;
            }
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }

        return true;
    }

    /// <summary>Returns true if the given event matches the pattern.</summary>
    public static bool Matches(string? patternJson, JsonNode? @event)
    {
        // A rule with no event pattern (e.g. a scheduled rule) never matches PutEvents traffic.
        if (string.IsNullOrWhiteSpace(patternJson))
        {
            return false;
        }

        JsonNode? pattern;
        try
        {
            pattern = JsonNode.Parse(patternJson!);
        }
        catch (JsonException)
        {
            return false;
        }

        return pattern is JsonObject obj && JsonPatternMatcher.Matches(obj, @event as JsonObject);
    }
}

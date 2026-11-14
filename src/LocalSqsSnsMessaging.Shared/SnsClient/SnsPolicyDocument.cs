using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalSqsSnsMessaging;

/// <summary>Helpers for SNS topic access policies held as JSON text.</summary>
internal static class SnsPolicyDocument
{
    /// <summary>Whether two policy documents say the same thing, whatever their layout.</summary>
    public static bool AreEquivalent(string a, string? b)
    {
        try
        {
            return JsonNode.DeepEquals(JsonNode.Parse(a), JsonNode.Parse(b ?? string.Empty));
        }
        catch (JsonException)
        {
            return string.Equals(a, b, StringComparison.Ordinal);
        }
    }
}

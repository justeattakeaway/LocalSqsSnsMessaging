using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalSqsSnsMessaging.EventBridge.Model;

namespace LocalSqsSnsMessaging;

/// <summary>Builds the event envelope EventBridge routes for each PutEvents entry.</summary>
internal static class EventBridgeEnvelope
{
    /// <summary>Builds the EventBridge event envelope for a PutEvents entry.</summary>
    public static JsonObject Build(PutEventsRequestEntry entry, string eventId, InMemoryAwsBus bus)
    {
        JsonNode? detail = null;
        if (!string.IsNullOrWhiteSpace(entry.Detail))
        {
            try
            {
                detail = JsonNode.Parse(entry.Detail!);
            }
            catch (JsonException)
            {
                detail = null;
            }
        }
        detail ??= new JsonObject();

        var time = entry.Time ?? bus.TimeProvider.GetUtcNow().UtcDateTime;

        var resources = new JsonArray();
        if (entry.Resources is not null)
        {
            foreach (var r in entry.Resources)
            {
                resources.Add((JsonNode?)r);
            }
        }

        return new JsonObject
        {
            ["version"] = "0",
            ["id"] = eventId,
            ["detail-type"] = entry.DetailType ?? string.Empty,
            ["source"] = entry.Source ?? string.Empty,
            ["account"] = bus.CurrentAccountId,
            ["time"] = time.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            ["region"] = bus.CurrentRegion,
            ["resources"] = resources,
            ["detail"] = detail
        };
    }
}

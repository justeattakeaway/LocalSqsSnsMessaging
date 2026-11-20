using System.Globalization;
using System.Text.Json.Nodes;

namespace InMemoryAws;

// Events AWS services put on a region's default bus (S3's "Object Created", ...).
internal sealed partial class InternalEventBridgeClient
{
    /// <summary>
    /// Puts an event an AWS service emits on the region's default event bus, and routes it through
    /// the bus's rules as PutEvents routes an entry: the envelope names the cloud's account, this
    /// region and the time now, to the second.
    /// </summary>
    /// <param name="source">The event's <c>source</c>, e.g. <c>aws.s3</c>.</param>
    /// <param name="detailType">The event's <c>detail-type</c>, e.g. <c>Object Created</c>.</param>
    /// <param name="resources">The ARNs of the resources the event is about.</param>
    /// <param name="detail">The event's <c>detail</c>.</param>
    internal void PutServiceEvent(string source, string detailType, IReadOnlyList<string> resources, JsonObject detail)
    {
        var eventBus = ResolveBus(DefaultBusName);
        var now = _cloud.TimeProvider.GetUtcNow();
        var resourceArray = new JsonArray();
        foreach (var resource in resources)
        {
            resourceArray.Add((JsonNode?)resource);
        }

        var envelope = new JsonObject
        {
            ["version"] = "0",
            ["id"] = Guid.NewGuid().ToString(),
            ["detail-type"] = detailType,
            ["source"] = source,
            ["account"] = _cloud.CurrentAccountId,
            ["time"] = now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["region"] = _resources.Region,
            ["resources"] = resourceArray,
            // Read back from JSON, as a PutEvents entry's Detail is, so rules match its values as they match any event's.
            ["detail"] = EventBridgeEnvelope.ParseDetail(detail.ToJsonString()),
        };

        EventBridgeEventDelivery.Route(eventBus, new EventBridgeEvent { Envelope = envelope, IngestionTime = now }, _cloud);
    }
}

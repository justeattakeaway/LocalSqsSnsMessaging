using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalSqsSnsMessaging.EventBridge.Model;
using LocalSqsSnsMessaging.Sqs.Model;

namespace LocalSqsSnsMessaging;

/// <summary>
/// An event a bus accepted: the envelope its rules match and its targets receive, plus what
/// EventBridge records about the event without putting it in the envelope.
/// </summary>
internal sealed class EventBridgeEvent
{
    /// <summary>The event envelope (<c>version</c>, <c>id</c>, <c>detail-type</c>, <c>source</c>, ..., <c>detail</c>).</summary>
    public required JsonObject Envelope { get; init; }

    /// <summary>When the bus received the event, which an explicit <c>Time</c> does not change (<c>aws.events.event.ingestion-time</c>).</summary>
    public required DateTimeOffset IngestionTime { get; init; }

    /// <summary>The entry's X-Ray trace header when it is well formed, which SQS targets receive as <c>AWSTraceHeader</c>; otherwise <see langword="null"/>.</summary>
    public string? TraceHeader { get; init; }
}

/// <summary>Builds the event envelope EventBridge routes for each PutEvents entry.</summary>
internal static class EventBridgeEnvelope
{
    /// <summary>The deepest nesting of objects and arrays a Detail may have, counting the Detail object itself.</summary>
    public const int MaxDetailDepth = 1000;

    /// <summary>
    /// Parses an entry's Detail, which must be a JSON object nested at most <see cref="MaxDetailDepth"/>
    /// levels deep; anything else (not JSON, an array, a string, ...) is <see langword="null"/>.
    /// </summary>
    public static JsonObject? ParseDetail(string detail)
    {
        try
        {
            return JsonNode.Parse(detail, documentOptions: new JsonDocumentOptions { MaxDepth = MaxDetailDepth }) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Builds the event for a validated PutEvents entry whose Detail parsed to <paramref name="detail"/>.</summary>
    public static EventBridgeEvent Build(PutEventsRequestEntry entry, JsonObject detail, string eventId, InMemoryAwsBus bus)
    {
        var ingestionTime = bus.TimeProvider.GetUtcNow();
        var time = entry.Time is { } sent ? AsUtc(sent) : ingestionTime.UtcDateTime;

        var resources = new JsonArray();
        foreach (var resource in entry.Resources ?? [])
        {
            resources.Add((JsonNode?)resource);
        }

        var envelope = new JsonObject
        {
            ["version"] = "0",
            ["id"] = eventId,
            ["detail-type"] = entry.DetailType,
            ["source"] = entry.Source,
            ["account"] = bus.CurrentAccountId,
            ["time"] = FormatTime(time),
            ["region"] = bus.CurrentRegion,
            ["resources"] = resources,
            ["detail"] = detail
        };

        return new EventBridgeEvent
        {
            Envelope = envelope,
            IngestionTime = ingestionTime,
            TraceHeader = entry.TraceHeader is { } header && IsWellFormedTraceHeader(header) ? header : null
        };
    }

    /// <summary>An envelope <c>time</c>: UTC to the second, e.g. <c>2024-03-04T05:06:07Z</c>.</summary>
    private static string FormatTime(DateTime utc) =>
        utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    // A Time without a zone is read as UTC, as the wire format (epoch seconds) intends.
    private static DateTime AsUtc(DateTime time) => time.Kind switch
    {
        DateTimeKind.Utc => time,
        DateTimeKind.Local => time.ToUniversalTime(),
        _ => DateTime.SpecifyKind(time, DateTimeKind.Utc)
    };

    // EventBridge accepts any TraceHeader but passes on only one SQS would keep.
    private static bool IsWellFormedTraceHeader(string header) =>
        SqsRequestValidation.IsWellFormedTraceHeader(new MessageSystemAttributeValue { DataType = "String", StringValue = header });
}

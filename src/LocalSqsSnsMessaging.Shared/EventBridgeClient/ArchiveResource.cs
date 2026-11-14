using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace LocalSqsSnsMessaging;

/// <summary>
/// In-memory representation of an EventBridge archive: the events its source bus's managed rule
/// (<see cref="ManagedRuleName"/>) has fed it, which replays read back.
/// </summary>
internal sealed class ArchiveResource
{
    /// <summary>The prefix of the managed rule EventBridge creates on the source bus for each archive.</summary>
    public const string ManagedRulePrefix = "Events-Archive-";

    /// <summary>An archive's state: archives are usable as soon as they are created, so always this.</summary>
    public const string Enabled = "ENABLED";

    private readonly object _eventsLock = new();
    private readonly List<ArchivedEvent> _events = [];
    private long _sizeBytes;

    public required string Name { get; init; }
    public required string Arn { get; init; }

    /// <summary>The ARN of the event bus the archive records, as given to CreateArchive.</summary>
    public required string EventSourceArn { get; init; }

    /// <summary>The name of the event bus the archive records.</summary>
    public required string EventBusName { get; init; }
    public string? Description { get; set; }

    /// <summary>The archive's own event pattern (without the replay exclusion its managed rule adds); null archives every event.</summary>
    public string? EventPattern { get; set; }
    public int RetentionDays { get; set; }
    public string? KmsKeyIdentifier { get; set; }
    public DateTimeOffset CreationTime { get; init; }

    /// <summary>The name of the rule that feeds this archive on its source bus.</summary>
    public string ManagedRuleName => ManagedRulePrefix + Name;

    public long EventCount
    {
        get
        {
            lock (_eventsLock)
            {
                return _events.Count;
            }
        }
    }

    public long SizeBytes
    {
        get
        {
            lock (_eventsLock)
            {
                return _sizeBytes;
            }
        }
    }

    /// <summary>Records an event envelope routed to the archive by its managed rule.</summary>
    public void Store(JsonObject envelope)
    {
        var json = envelope.ToJsonString();
        var time = DateTimeOffset.TryParse(envelope["time"]?.GetValue<string>(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;

        lock (_eventsLock)
        {
            _events.Add(new ArchivedEvent(time, json));
            _sizeBytes += Encoding.UTF8.GetByteCount(json);
        }
    }

    /// <summary>The archived envelopes whose event time falls within [start, end], oldest first.</summary>
    public List<ArchivedEvent> EventsBetween(DateTimeOffset start, DateTimeOffset end)
    {
        lock (_eventsLock)
        {
            return [.. _events.Where(e => e.Time >= start && e.Time <= end).OrderBy(e => e.Time)];
        }
    }

    /// <summary>
    /// The pattern of the archive's managed rule: the archive's own pattern plus
    /// <c>"replay-name": [{"exists": false}]</c>, so replayed events are never archived again.
    /// </summary>
    public string ManagedRulePattern()
    {
        var pattern = string.IsNullOrEmpty(EventPattern)
            ? new JsonObject()
            : JsonNode.Parse(EventPattern!) as JsonObject ?? new JsonObject();
        pattern["replay-name"] = new JsonArray(new JsonObject { ["exists"] = false });
        return pattern.ToJsonString();
    }
}

/// <summary>An event as an archive stored it: its event time and its envelope JSON.</summary>
internal sealed record ArchivedEvent(DateTimeOffset Time, string Envelope);

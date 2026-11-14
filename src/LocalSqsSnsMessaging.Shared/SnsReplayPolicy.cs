using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalSqsSnsMessaging;

/// <summary>
/// A subscription's <c>ReplayPolicy</c>
/// (<c>{"PointType": "Timestamp", "StartingPoint": "...", "EndingPoint": "..."}</c>): the span of a
/// FIFO topic's archive to replay to it.
/// See https://docs.aws.amazon.com/sns/latest/dg/message-archiving-and-replay-subscriber.html
/// </summary>
internal static class SnsReplayPolicy
{
    /// <summary><c>ReplayStatus</c> while a replay waits for its subscription to be confirmed.</summary>
    public const string Pending = "Pending";

    /// <summary><c>ReplayStatus</c> once every selected archived message has been delivered.</summary>
    public const string Completed = "Completed";

    /// <summary>
    /// Refuses a replay policy that isn't valid, or that names a topic without an <c>ArchivePolicy</c>
    /// to replay from.
    /// </summary>
    public static void Validate(string policy, SnsTopicResource? topic)
    {
        bool archiving;
        if (topic is null)
        {
            archiving = false;
        }
        else
        {
            lock (topic.SubscriptionGate)
            {
                archiving = topic.Attributes.ContainsKey(SnsTopicAttributes.ArchivePolicy);
            }
        }

        if (!archiving)
        {
            throw new InternalInvalidParameterException("Invalid parameter: ReplayPolicy: Topic ArchivePolicy attribute is missing");
        }

        Parse(policy);
    }

    /// <summary>The span a valid replay policy selects: from its starting point to its ending point, if any.</summary>
    public static (DateTimeOffset Start, DateTimeOffset? End) Parse(string policy)
    {
        JsonObject? json;
        try
        {
            json = JsonNode.Parse(policy) as JsonObject;
        }
        catch (JsonException)
        {
            json = null;
        }

        if (json is null)
        {
            throw new InternalInvalidParameterException("Invalid parameter: ReplayPolicy: Replay policy must be a JSON object");
        }

        if (!string.Equals(StringProperty(json, "PointType"), "Timestamp", StringComparison.Ordinal))
        {
            throw new InternalInvalidParameterException("Invalid parameter: ReplayPolicy: PointType must be Timestamp");
        }

        var start = Timestamp(json, "StartingPoint")
                    ?? throw new InternalInvalidParameterException("Invalid parameter: ReplayPolicy: StartingPoint must be a timestamp");
        var end = Timestamp(json, "EndingPoint");
        if (json.ContainsKey("EndingPoint") && end is null)
        {
            throw new InternalInvalidParameterException("Invalid parameter: ReplayPolicy: EndingPoint must be a timestamp");
        }

        return (start, end);
    }

    private static string? StringProperty(JsonObject json, string name) =>
        json[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static DateTimeOffset? Timestamp(JsonObject json, string name) =>
        DateTimeOffset.TryParse(StringProperty(json, name), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp)
            ? timestamp
            : null;
}
